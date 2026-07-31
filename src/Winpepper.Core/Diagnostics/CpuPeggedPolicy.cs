namespace Winpepper.Core.Diagnostics;

/// <summary>
/// Pure decision for the status pill's "CPU pegged" indicator: is the
/// machine's overall CPU busy enough around dictation start that this
/// dictation may be slower? Sampling lives in SessionViewModel (riding the
/// pill's existing 100 ms tick); the sampler is
/// ProcessResourceSampler.SystemTimes and the percent math is
/// DictationTimingSummary.SystemCpuPercent. Linux-tested by design.
/// </summary>
public static class CpuPeggedPolicy
{
    /// <summary>Overall system CPU %, at or above which we show the meter.</summary>
    public const int SystemCpuPeggedThresholdPercent = 75;

    /// <summary>Evaluate on the Nth 100 ms pill tick after recording starts
    /// (~400 ms window -- short enough that the indicator appears well within
    /// ~1 s of the pill showing, long enough for a stable GetSystemTimes delta).</summary>
    public const int SampleAfterTicks = 4;

    public static bool IsPegged(int? systemCpuPercent)
        => systemCpuPercent is { } pct && pct >= SystemCpuPeggedThresholdPercent;
}
