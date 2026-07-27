namespace AsrLatencyBench;

/// <summary>A point-in-time reading of the CURRENT process's resource use.
/// CpuSeconds = total processor time (user + privileged). PeakWorkingSetBytes
/// is the process-lifetime peak (monotonic; it cannot be reset per clip).
/// GPU/Vulkan usage is not measured by this type at all.</summary>
public sealed record ResourceSample(double CpuSeconds, long PeakWorkingSetBytes);

public static class ResourceUsage
{
    public static ResourceSample Capture()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return new ResourceSample(p.TotalProcessorTime.TotalSeconds, p.PeakWorkingSet64);
    }

    public static double CpuDelta(ResourceSample before, ResourceSample after)
        => Math.Max(0, after.CpuSeconds - before.CpuSeconds);

    /// <summary>Real-time factor: processing seconds per second of audio.
    /// 0 when there is no audio (never divides by zero).</summary>
    public static double Rtf(double processingSeconds, double audioSeconds)
        => audioSeconds <= 0 ? 0 : processingSeconds / audioSeconds;

    public static double ToMb(long bytes) => bytes / (1024.0 * 1024.0);
}
