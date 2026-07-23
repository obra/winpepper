namespace Winpepper.Audio;

/// <summary>
/// Pure-managed audio-energy helpers (Bug 2 — undetectable silent capture).
/// OS mic-mute, the Windows privacy toggle, or a Bluetooth profile hiccup can
/// hand us zero-filled buffers that are indistinguishable from healthy audio.
/// A cheap RMS check over a whole session lets the host tell the user "no audio
/// detected" instead of silently transcribing nothing.
/// </summary>
public static class AudioEnergy
{
    /// <summary>
    /// Sessions whose RMS is below this are "essentially zero energy" (~-80 dBFS).
    /// This is a ZERO-ENERGY / dead-device detector, NOT a voice-activity detector:
    /// a live mic's noise floor (~-40..-65 dBFS) stays above this even during long
    /// pauses, so only muted / privacy-off / zero-filled capture falls below it. Do
    /// not "improve" this into a VAD or raise it toward speech levels.
    /// </summary>
    public const double SilenceRmsThreshold = 1e-4;

    /// <summary>Root-mean-square amplitude of a mono float frame (0 for empty).</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0.0;
        double sumSq = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            sumSq += v * v;
        }
        return Math.Sqrt(sumSq / samples.Length);
    }

    /// <summary>
    /// True when a non-empty session captured essentially zero energy. Empty
    /// input returns false — "nothing captured" is a distinct condition the
    /// caller guards with a length check before deciding to warn.
    /// </summary>
    public static bool IsSessionSilent(ReadOnlySpan<float> samples, double rmsThreshold = SilenceRmsThreshold)
        => samples.Length > 0 && Rms(samples) < rmsThreshold;
}
