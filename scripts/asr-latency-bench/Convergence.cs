namespace AsrLatencyBench;

/// <summary>One pass's convergence measurement over the pooled per-clip medians
/// of the mode's speed metric (streaming: post-stop latency ms; batch: batch
/// transcribe ms). Stable = the mean changed by less than 2% from the previous
/// pass's mean. The run has converged when two CONSECUTIVE passes are stable.
/// CiHalfWidthMs/RatioToMean describe BETWEEN-CLIP spread and are reported as
/// diagnostics only: on corpus-v1 the between-clip CV is ~0.25, so a CI-ratio
/// exit criterion is structurally unreachable (validated against the baseline
/// results.json) — extra passes stabilize per-clip medians, which is exactly
/// what DeltaFromPrevious measures.</summary>
public sealed record ConvergencePoint(
    int Pass, double MeanMs, double CiHalfWidthMs, double RatioToMean,
    double DeltaFromPrevious, bool Stable);

public static class Convergence
{
    /// <summary>The mean must move by less than this fraction between passes.</summary>
    public const double StableRatio = 0.02;

    public static double Mean(IReadOnlyList<double> values)
        => values.Count == 0 ? 0 : values.Average();

    /// <summary>Sample standard deviation (n-1). 0 when fewer than 2 values.</summary>
    public static double SampleStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = Mean(values);
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    /// <summary>95% confidence-interval half-width of the mean, normal
    /// approximation: 1.96 * sd / sqrt(n). 0 when fewer than 2 values.</summary>
    public static double CiHalfWidth95(IReadOnlyList<double> values)
        => values.Count < 2 ? 0 : 1.96 * SampleStdDev(values) / Math.Sqrt(values.Count);

    /// <summary>Nearest-rank median (same convention as EvalResults.Percentile).</summary>
    public static double Median(IReadOnlyList<double> values)
        => EvalResults.Percentile(values.OrderBy(v => v).ToArray(), 0.5);

    /// <summary>previousMeanMs <= 0 means "no previous pass" (first pass):
    /// DeltaFromPrevious is Infinity and the point is never stable.</summary>
    public static ConvergencePoint Evaluate(int pass, IReadOnlyList<double> perClipMedians,
        double previousMeanMs)
    {
        var mean = Mean(perClipMedians);
        var half = CiHalfWidth95(perClipMedians);
        var ratio = mean <= 0 ? double.PositiveInfinity : half / mean;
        var delta = mean > 0 && previousMeanMs > 0
            ? Math.Abs(mean - previousMeanMs) / previousMeanMs
            : double.PositiveInfinity;
        var stable = perClipMedians.Count >= 2 && mean > 0 && delta < StableRatio;
        return new ConvergencePoint(pass, mean, half, ratio, delta, stable);
    }

    public static bool Converged(IReadOnlyList<ConvergencePoint> trace)
        => trace.Count >= 2 && trace[^1].Stable && trace[^2].Stable;
}
