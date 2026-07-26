using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Winpepper.Asr;

/// <summary>
/// Parakeet TDT v3 ONNX session. Loads encoder + decoder_joint, performs a
/// greedy TDT decode (port of parakeet-rs/src/model_tdt.rs).
///
/// Decoder hidden state shape is the parakeet-rs export convention: [2, 1, 640].
/// Vocab size is inferred from vocab.txt (last token is the blank).
/// </summary>
public sealed class ParakeetSession : IParakeetBackend, IDisposable
{
    private const int DecoderHiddenLayers = 2;
    private const int DecoderHiddenDim = 640;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderJoint;
    private readonly Vocabulary _vocab;
    private readonly MelFeatureExtractor _features;

    public Vocabulary Vocab => _vocab;

    /// <summary>True when the session is using the DirectML EP; false on CPU fallback.</summary>
    public bool UsingDirectML { get; }

    public int VocabSize => _vocab.Size;
    public int BlankId => _vocab.BlankId;
    int IParakeetBackend.DecoderHiddenLayers => DecoderHiddenLayers;
    int IParakeetBackend.DecoderHiddenDim => DecoderHiddenDim;

    public string DecodeTokens(IEnumerable<int> tokenIds) => _vocab.Decode(tokenIds);

    public ParakeetSession(string modelDir)
    {
        var (encoderPath, decoderPath, vocabPath) = ResolvePaths(modelDir);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        // Try DirectML first (the design's chosen acceleration backend).
        // Fall back to CPU when DirectML isn't available — common on headless
        // VMs / CI runners with no DX12 adapter. Real Windows users with a GPU
        // get DirectML acceleration.
        try
        {
            options.EnableMemoryPattern = false; // DirectML EP requirement
            options.AppendExecutionProvider_DML(0);
            UsingDirectML = true;
        }
        catch (OnnxRuntimeException)
        {
            // Reset options for CPU EP.
            options.Dispose();
            options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            UsingDirectML = false;
        }

        _encoder = new InferenceSession(encoderPath, options);
        _decoderJoint = new InferenceSession(decoderPath, options);
        _vocab = Vocabulary.FromFile(vocabPath);
        _features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3);
    }

    private static (string Encoder, string Decoder, string Vocab) ResolvePaths(string dir)
    {
        string Find(params string[] names)
        {
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException($"None of {string.Join(", ", names)} found in {dir}");
        }
        return (
            Find("encoder-model.int8.onnx", "encoder-model.onnx", "encoder.onnx"),
            Find("decoder_joint-model.int8.onnx", "decoder_joint-model.onnx", "decoder_joint.onnx"),
            Find("vocab.txt"));
    }

    /// <summary>
    /// True when <paramref name="modelDir"/> contains the encoder, decoder, and
    /// vocab files this session needs. Lets callers (e.g. the app pipeline)
    /// detect the missing-model condition without paying for a session load or
    /// handling the constructor's <see cref="FileNotFoundException"/>.
    /// </summary>
    public static bool ModelFilesPresent(string modelDir)
    {
        try { ResolvePaths(modelDir); return true; }
        catch (FileNotFoundException) { return false; }
    }

    public ParakeetTranscript Transcribe(ReadOnlySpan<float> samples16k)
    {
        var features = _features.Extract(samples16k); // [T, 128]
        var enc = Encode(features);
        var state = new TdtDecoderState(DecoderHiddenLayers, DecoderHiddenDim, _vocab.BlankId);
        var tokens = new List<int>();
        var frameIndices = new List<int>();
        var durations = new List<int>();
        TdtGreedyDecoder.Decode(this, enc, state, tokens, frameIndices, durations);
        return new ParakeetTranscript(_vocab.Decode(tokens), tokens, frameIndices, durations);
    }

    public EncoderOutput Encode(float[,] features)
    {
        var time = features.GetLength(0);
        var feat = features.GetLength(1);

        // Encoder expects [batch=1, feature_size, time].
        var input = new float[1 * feat * time];
        for (var t = 0; t < time; t++)
            for (var f = 0; f < feat; f++)
                input[f * time + t] = features[t, f];

        var audioSignal = new DenseTensor<float>(input, new[] { 1, feat, time });
        var length = new DenseTensor<long>(new long[] { time }, new[] { 1 });

        using var results = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", audioSignal),
            NamedOnnxValue.CreateFromTensor("length", length),
        });

        var outTensor = results.First(r => r.Name == "outputs").AsTensor<float>();
        var lengths   = results.First(r => r.Name == "encoded_lengths").AsTensor<long>();

        // Encoder outputs [B=1, D=1024, T'] for Parakeet TDT v3.
        var b = (int)outTensor.Dimensions[0];
        var d = (int)outTensor.Dimensions[1];
        var tprime = (int)outTensor.Dimensions[2];
        if (b != 1) throw new InvalidOperationException("Batch != 1");
        var flat = new float[d * tprime];
        var idx = 0;
        foreach (var v in outTensor) flat[idx++] = v;
        return new EncoderOutput(flat, (int)lengths[0], d, tprime);
    }

    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        var encFrame = new DenseTensor<float>(encoderFrame, new[] { 1, encoderFrame.Length, 1 });
        var targets = new DenseTensor<int>(new[] { lastToken }, new[] { 1, 1 });
        var targetLen = new DenseTensor<int>(new[] { 1 }, new[] { 1 });
        var sh = new DenseTensor<float>(stateH, new[] { DecoderHiddenLayers, 1, DecoderHiddenDim });
        var sc = new DenseTensor<float>(stateC, new[] { DecoderHiddenLayers, 1, DecoderHiddenDim });

        using var results = _decoderJoint.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encFrame),
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("target_length", targetLen),
            NamedOnnxValue.CreateFromTensor("input_states_1", sh),
            NamedOnnxValue.CreateFromTensor("input_states_2", sc),
        });

        var logits = results.First(r => r.Name == "outputs").AsTensor<float>();
        var flat = new float[logits.Length];
        var idx = 0;
        foreach (var v in logits) flat[idx++] = v;

        var newH = results.First(r => r.Name == "output_states_1").AsTensor<float>();
        var newC = results.First(r => r.Name == "output_states_2").AsTensor<float>();
        var h = new float[newH.Length]; var hi = 0; foreach (var v in newH) h[hi++] = v;
        var c = new float[newC.Length]; var ci = 0; foreach (var v in newC) c[ci++] = v;
        return new DecoderJointResult(flat, h, c);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}
