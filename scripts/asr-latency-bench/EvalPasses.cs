namespace AsrLatencyBench;

/// <summary>Aggregates for one full pass over the corpus. "Latency" is the
/// mode's speed metric: streaming = post-stop latency (FinishAsync ms);
/// batch = whole-file batch transcribe ms.</summary>
public sealed record PassSummary(
    int Pass,
    long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
    double CpuSeconds, double PeakMemoryMb, double MeanRtf,
    double? MeanWer, int FailedCount);

public static class EvalPasses
{
    public static PassSummary Summarize(
        int pass,
        IReadOnlyList<double> latenciesMs,
        IReadOnlyList<double> rtfs,
        IReadOnlyList<double> wers,
        double cpuSeconds,
        long peakWorkingSetBytes,
        int failedCount)
    {
        var sorted = latenciesMs.OrderBy(v => v).ToArray();
        return new PassSummary(
            Pass: pass,
            LatencyP50Ms: (long)EvalResults.Percentile(sorted, 0.5),
            LatencyP90Ms: (long)EvalResults.Percentile(sorted, 0.9),
            LatencyMaxMs: sorted.Length == 0 ? 0 : (long)sorted[^1],
            CpuSeconds: Math.Round(cpuSeconds, 3),
            PeakMemoryMb: Math.Round(ResourceUsage.ToMb(peakWorkingSetBytes), 1),
            MeanRtf: rtfs.Count == 0 ? 0 : Math.Round(rtfs.Average(), 4),
            MeanWer: wers.Count == 0 ? null : Math.Round(wers.Average(), 4),
            FailedCount: failedCount);
    }
}
