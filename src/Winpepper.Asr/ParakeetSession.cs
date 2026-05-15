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
public sealed class ParakeetSession : IDisposable
{
    private const int MaxTokensPerStep = 10;
    private const int DecoderHiddenLayers = 2;
    private const int DecoderHiddenDim = 640;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderJoint;
    private readonly Vocabulary _vocab;
    private readonly MelFeatureExtractor _features;

    public Vocabulary Vocab => _vocab;

    /// <summary>True when the session is using the DirectML EP; false on CPU fallback.</summary>
    public bool UsingDirectML { get; }

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

    public ParakeetTranscript Transcribe(ReadOnlySpan<float> samples16k)
    {
        var features = _features.Extract(samples16k); // [T, 128]
        var (encoderOut, encoderLen, encoderDim, encoderTime) = RunEncoder(features);
        return GreedyDecode(encoderOut, encoderLen, encoderDim, encoderTime);
    }

    private (float[] EncoderOut, int Len, int Dim, int Time) RunEncoder(float[,] features)
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
        return (flat, (int)lengths[0], d, tprime);
    }

    private ParakeetTranscript GreedyDecode(float[] encoderOut, int validLen, int d, int tprime)
    {
        var vocabSize = _vocab.Size;
        var blankId = _vocab.BlankId;

        var stateH = new float[DecoderHiddenLayers * 1 * DecoderHiddenDim];
        var stateC = new float[DecoderHiddenLayers * 1 * DecoderHiddenDim];
        var lastToken = blankId;

        var tokens = new List<int>();
        var frameIndices = new List<int>();
        var durations = new List<int>();

        var t = 0;
        var emitted = 0;
        var frameBuf = new float[d];

        while (t < Math.Min(tprime, validLen))
        {
            // encoderOut is laid out [D, T'] row-major: the D-vector at time t is
            // {encoderOut[d_idx * T' + t] for d_idx in 0..D}.
            for (var k = 0; k < d; k++) frameBuf[k] = encoderOut[k * tprime + t];
            var encFrame = new DenseTensor<float>(frameBuf, new[] { 1, d, 1 });
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

            // Pick best token from first vocab_size logits.
            var bestToken = 0; var bestVal = float.NegativeInfinity;
            for (var i = 0; i < vocabSize; i++)
                if (flat[i] > bestVal) { bestVal = flat[i]; bestToken = i; }

            // Pick best duration from remaining logits.
            var durCount = flat.Length - vocabSize;
            var bestDur = 0; var bestDurVal = float.NegativeInfinity;
            for (var i = 0; i < durCount; i++)
                if (flat[vocabSize + i] > bestDurVal) { bestDurVal = flat[vocabSize + i]; bestDur = i; }

            if (bestToken != blankId)
            {
                tokens.Add(bestToken);
                frameIndices.Add(t);
                durations.Add(bestDur);
                lastToken = bestToken;
                emitted++;

                var newH = results.First(r => r.Name == "output_states_1").AsTensor<float>();
                var newC = results.First(r => r.Name == "output_states_2").AsTensor<float>();
                var hi = 0; foreach (var v in newH) stateH[hi++] = v;
                var ci = 0; foreach (var v in newC) stateC[ci++] = v;
            }

            if (bestDur > 0)
            {
                t += bestDur;
                emitted = 0;
            }
            else if (bestToken == blankId || emitted >= MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
        }

        var text = _vocab.Decode(tokens);
        return new ParakeetTranscript(text, tokens, frameIndices, durations);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}
