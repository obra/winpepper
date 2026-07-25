namespace Winpepper.Asr;

/// <summary>Greedy TDT decode state carried across encoder segments (chunks).</summary>
public sealed class TdtDecoderState
{
    public float[] StateH { get; set; }
    public float[] StateC { get; set; }
    public int LastToken { get; set; }

    /// <summary>Frame-advance overshoot left over from the previous segment: the TDT
    /// duration head can skip past a segment's end; the skip continues into the next.</summary>
    public int CarryAdvance { get; set; }

    public TdtDecoderState(int hiddenLayers, int hiddenDim, int blankId)
    {
        StateH = new float[hiddenLayers * 1 * hiddenDim];
        StateC = new float[hiddenLayers * 1 * hiddenDim];
        LastToken = blankId;
    }
}

/// <summary>
/// Pure greedy TDT decode loop — an exact port of the former
/// ParakeetSession.GreedyDecode — parameterized over a backend and a carried
/// state so it can run over a whole utterance (batch) or over successive
/// encoder segments (streaming).
/// </summary>
public static class TdtGreedyDecoder
{
    public const int MaxTokensPerStep = 10;

    /// <summary>
    /// Decode encoder frames [startFrame + state.CarryAdvance, min(Frames, ValidLen))
    /// of <paramref name="enc"/>, mutating <paramref name="state"/> and appending to
    /// the token lists. <paramref name="frameIndexOffset"/> is added to recorded
    /// frame indices so streaming callers report utterance-global positions.
    /// </summary>
    public static void Decode(
        IParakeetBackend backend,
        EncoderOutput enc,
        TdtDecoderState state,
        List<int> tokens,
        List<int> frameIndices,
        List<int> durations,
        int startFrame = 0,
        int frameIndexOffset = 0)
    {
        var vocabSize = backend.VocabSize;
        var blankId = backend.BlankId;
        var limit = Math.Min(enc.Frames, enc.ValidLen);

        var t = startFrame + state.CarryAdvance;
        state.CarryAdvance = 0;
        var emitted = 0;
        var frameBuf = new float[enc.Dim];

        while (t < limit)
        {
            for (var k = 0; k < enc.Dim; k++) frameBuf[k] = enc.Data[k * enc.Frames + t];
            var step = backend.DecodeJoint(frameBuf, state.LastToken, state.StateH, state.StateC);
            var flat = step.Logits;

            var bestToken = 0; var bestVal = float.NegativeInfinity;
            for (var i = 0; i < vocabSize; i++)
                if (flat[i] > bestVal) { bestVal = flat[i]; bestToken = i; }

            var durCount = flat.Length - vocabSize;
            var bestDur = 0; var bestDurVal = float.NegativeInfinity;
            for (var i = 0; i < durCount; i++)
                if (flat[vocabSize + i] > bestDurVal) { bestDurVal = flat[vocabSize + i]; bestDur = i; }

            if (bestToken != blankId)
            {
                tokens.Add(bestToken);
                frameIndices.Add(frameIndexOffset + t);
                durations.Add(bestDur);
                state.LastToken = bestToken;
                emitted++;
                state.StateH = step.StateH;
                state.StateC = step.StateC;
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

        state.CarryAdvance = Math.Max(0, t - limit);
    }
}
