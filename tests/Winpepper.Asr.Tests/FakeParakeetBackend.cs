using Winpepper.Asr;

namespace Winpepper.Asr.Tests;

/// <summary>
/// Frame-local fake backend: Encode passes mel frame t's first component through
/// as encoder frame t (subsampling configurable), and DecodeJoint behavior is
/// scripted per call. Records calls so tests can assert chunking/state mechanics.
/// </summary>
public sealed class FakeParakeetBackend : IParakeetBackend
{
    public int VocabSize { get; init; } = 8; // tokens 0..6, blank = 7
    public int BlankId => VocabSize - 1;
    public int DecoderHiddenLayers => 2;
    public int DecoderHiddenDim => 4;
    public int SubsamplingFactor { get; init; } = 1;
    public int DurationBins { get; init; } = 5;

    public List<int> EncodeMelFrameCounts { get; } = new();
    public List<(float FirstComponent, int LastToken)> JointCalls { get; } = new();

    /// <summary>Optional scripted joint. Args: encoder frame, lastToken. Default: always blank, advance 1.</summary>
    public Func<float[], int, DecoderJointResult>? Joint { get; init; }

    /// <summary>Optional Encode override for failure injection (called with mel frame count).</summary>
    public Action<int>? OnEncode { get; init; }

    public EncoderOutput Encode(float[,] melFrames)
    {
        var tIn = melFrames.GetLength(0);
        OnEncode?.Invoke(tIn);
        EncodeMelFrameCounts.Add(tIn);
        var tOut = Math.Max(1, tIn / SubsamplingFactor);
        const int dim = 2;
        var data = new float[dim * tOut];
        for (var t = 0; t < tOut; t++)
        {
            data[0 * tOut + t] = melFrames[t * SubsamplingFactor, 0];
            data[1 * tOut + t] = t;
        }
        return new EncoderOutput(data, tOut, dim, tOut);
    }

    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        JointCalls.Add((encoderFrame[0], lastToken));
        if (Joint is not null) return Joint(encoderFrame, lastToken);
        var logits = new float[VocabSize + DurationBins];
        logits[BlankId] = 10f;          // blank wins
        logits[VocabSize + 1] = 10f;    // duration 1
        return new DecoderJointResult(logits, stateH, stateC);
    }

    public string DecodeTokens(IEnumerable<int> tokenIds) => string.Join(",", tokenIds);

    /// <summary>Build a joint result emitting <paramref name="token"/> with duration <paramref name="dur"/>.</summary>
    public DecoderJointResult Emit(int token, int dur, float[]? h = null, float[]? c = null)
    {
        var logits = new float[VocabSize + DurationBins];
        logits[token] = 10f;
        logits[VocabSize + dur] = 10f;
        return new DecoderJointResult(
            logits,
            h ?? new float[DecoderHiddenLayers * DecoderHiddenDim],
            c ?? new float[DecoderHiddenLayers * DecoderHiddenDim]);
    }
}
