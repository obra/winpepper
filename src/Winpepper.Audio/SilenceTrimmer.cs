namespace Winpepper.Audio;

/// <summary>Outcome of <see cref="SilenceTrimmer.Trim"/>.</summary>
public readonly struct TrimResult
{
    /// <summary>Samples to send to ASR. Empty when <see cref="IsSilent"/> is true.</summary>
    public required float[] Trimmed { get; init; }

    /// <summary>Total milliseconds of silence removed (0 when nothing was trimmed).</summary>
    public required int RemovedMs { get; init; }

    /// <summary>Number of below-threshold runs that were compressed.</summary>
    public required int RunsTrimmed { get; init; }

    /// <summary>
    /// True when the recording contains no speech (live mic, nobody spoke).
    /// The caller DROPs such a dictation. Distinct from AudioEnergy's dead-mic
    /// detector: this is a voice-presence check over frame-RMS percentiles.
    /// </summary>
    public required bool IsSilent { get; init; }
}

/// <summary>
/// Pure silence trimmer for a FINISHED 16 kHz mono float session buffer.
///
/// Parameters are FIXED by an on-device experiment (45 real archived dictations
/// transcribed with the real parakeet model, original vs trimmed at caps of
/// 300/500/800/1200/2000 ms). At cap=1200 the experiment removed 59.0 s of
/// audio / saved 11.4 s of ASR time across 45 files with only 5 transcripts
/// changed (9 word-edits, ALL cosmetic — capitalization / comma-vs-period).
/// cap=500 caused real word damage ("ligh", "Great"->"Right"); cap=800 injected
/// a disfluency. 1200 ms is the chosen safe point. Do NOT re-derive these.
///
/// Reuses <see cref="AudioEnergy.Rms"/>. Does NOT touch
/// <see cref="AudioEnergy.SilenceRmsThreshold"/> (a different, dead-mic concern).
/// </summary>
public static class SilenceTrimmer
{
    private const int SampleRate = 16000;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320

    /// <summary>Milliseconds of silence kept adjacent to each speech edge.</summary>
    private const int KeepMsPerEdge = 600;
    private const int KeepFramesPerEdge = KeepMsPerEdge / FrameMs; // 30
    // Interior keep budget is 2 * KeepMsPerEdge = 1200 ms (the experiment cap).

    private const double NoiseFloorPercentile = 0.10;
    private const double SpeechLevelPercentile = 0.90;
    private const double ThresholdNoiseMultiplier = 3.0;
    private const double ThresholdAbsFloor = 0.002;
    private const double SpeechCapFactor = 0.15;

    /// <summary>Below this 90th-percentile RMS the recording has no speech.</summary>
    private const double SilentSpeechLevel = 0.004;

    public static TrimResult Trim(ReadOnlySpan<float> samples)
    {
        var n = samples.Length;
        var frameCount = n / FrameSamples;
        if (frameCount == 0)
        {
            // Fewer than one full frame: nothing to analyze. Not "silent" —
            // empty/cancel captures are guarded by the caller's length check.
            return new TrimResult
            {
                Trimmed = samples.ToArray(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = false,
            };
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
            rms[f] = AudioEnergy.Rms(samples.Slice(f * FrameSamples, FrameSamples));

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var speechLevel = Percentile(sorted, SpeechLevelPercentile);

        if (speechLevel < SilentSpeechLevel)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
            };
        }

        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive threshold. (Task 3 adds the 0.15*speechLevel fail-safe cap.)
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);

        var isSilence = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
            isSilence[f] = rms[f] < threshold;

        // Walk contiguous silence runs; build the ordered list of whole-frame
        // segments to KEEP. Interior runs keep 600 ms per speech edge; edge runs
        // keep 600 ms adjacent to their single speech edge; the middle is deleted.
        var kept = new List<(int start, int len)>();
        var removedFrames = 0;
        var runsTrimmed = 0;

        var i = 0;
        while (i < frameCount)
        {
            if (!isSilence[i])
            {
                AppendKeep(kept, i, 1);
                i++;
                continue;
            }

            var runStart = i;
            while (i < frameCount && isSilence[i]) i++;
            var runEnd = i; // exclusive
            var runLen = runEnd - runStart;

            var hasLeftSpeech = runStart > 0;
            var hasRightSpeech = runEnd < frameCount;
            var edges = (hasLeftSpeech ? 1 : 0) + (hasRightSpeech ? 1 : 0);
            var keepBudget = edges * KeepFramesPerEdge;

            if (edges > 0 && runLen > keepBudget)
            {
                if (hasLeftSpeech) AppendKeep(kept, runStart, KeepFramesPerEdge);
                if (hasRightSpeech) AppendKeep(kept, runEnd - KeepFramesPerEdge, KeepFramesPerEdge);
                removedFrames += runLen - keepBudget;
                runsTrimmed++;
            }
            else
            {
                // Short enough to keep whole, or an all-silence buffer with no
                // speech edge (defensive; the IsSilent gate normally catches it).
                AppendKeep(kept, runStart, runLen);
            }
        }

        var keptFrames = 0;
        foreach (var seg in kept) keptFrames += seg.len;
        var tail = n - frameCount * FrameSamples;
        var outBuf = new float[keptFrames * FrameSamples + tail];

        var w = 0;
        foreach (var (start, len) in kept)
        {
            samples.Slice(start * FrameSamples, len * FrameSamples).CopyTo(outBuf.AsSpan(w));
            w += len * FrameSamples;
        }
        if (tail > 0)
            samples.Slice(frameCount * FrameSamples, tail).CopyTo(outBuf.AsSpan(w));

        return new TrimResult
        {
            Trimmed = outBuf,
            RemovedMs = removedFrames * FrameMs,
            RunsTrimmed = runsTrimmed,
            IsSilent = false,
        };
    }

    private static void AppendKeep(List<(int start, int len)> segs, int start, int len)
    {
        if (len <= 0) return;
        if (segs.Count > 0)
        {
            var last = segs[^1];
            if (last.start + last.len == start)
            {
                segs[^1] = (last.start, last.len + len);
                return;
            }
        }
        segs.Add((start, len));
    }

    private static double Percentile(double[] sortedAsc, double p)
    {
        if (sortedAsc.Length == 0) return 0.0;
        var idx = (int)Math.Floor(p * (sortedAsc.Length - 1));
        if (idx < 0) idx = 0;
        if (idx >= sortedAsc.Length) idx = sortedAsc.Length - 1;
        return sortedAsc[idx];
    }
}
