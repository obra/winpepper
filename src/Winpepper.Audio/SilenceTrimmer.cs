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

        // Increment A (Task 1): speech present — pass the whole buffer through.
        // Trimming is added in Task 2.
        return new TrimResult
        {
            Trimmed = samples.ToArray(),
            RemovedMs = 0,
            RunsTrimmed = 0,
            IsSilent = false,
        };
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
