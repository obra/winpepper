using System.Text.Json;

namespace AsrLatencyBench;

/// <summary>One model's row in the cross-model comparison. Numbers only —
/// no transcript or reference text ever lands in comparison.json.</summary>
public sealed record ModelComparisonEntry(
    string Model, string Mode, string? Language, int Passes, bool Converged,
    int ClipCount, int ScoredCount, double? MeanWer, double? MedianWer, double? MeanCer,
    long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
    double CpuSecondsTotal, double PeakMemoryMb, double MeanRtf,
    int FallbackCount, int TruncatedCount, int FailedCount, int UnstableTranscriptCount,
    IReadOnlyList<ConvergencePoint> ConvergenceTrace,
    IReadOnlyList<PassSummary> PassSummaries);

public sealed record ComparisonReport(
    string DateUtc, string Corpus, string ResourceNote,
    IReadOnlyList<ModelComparisonEntry> Models);

public static class EvalComparison
{
    public static EvalReport Parse(string resultsJson)
        => JsonSerializer.Deserialize<EvalReport>(resultsJson, EvalResults.JsonOpts)
           ?? throw new InvalidOperationException("results.json parsed to null");

    public static ModelComparisonEntry FromReport(EvalReport r) => new(
        Model: r.Info.SpeechModel,
        Mode: r.Info.Mode,
        Language: r.Info.Language,
        Passes: r.Info.Passes,
        Converged: r.Info.Converged,
        ClipCount: r.Summary.ClipCount,
        ScoredCount: r.Summary.ScoredCount,
        MeanWer: r.Summary.MeanWer,
        MedianWer: r.Summary.MedianWer,
        MeanCer: r.Summary.MeanCer,
        LatencyP50Ms: r.Summary.LatencyP50Ms,
        LatencyP90Ms: r.Summary.LatencyP90Ms,
        LatencyMaxMs: r.Summary.LatencyMaxMs,
        CpuSecondsTotal: r.Summary.CpuSecondsTotal,
        PeakMemoryMb: r.Summary.PeakMemoryMb,
        MeanRtf: r.Summary.MeanRtf,
        FallbackCount: r.Summary.FallbackCount,
        TruncatedCount: r.Summary.TruncatedCount,
        FailedCount: r.Summary.FailedCount,
        UnstableTranscriptCount: r.Summary.UnstableTranscriptCount,
        ConvergenceTrace: r.ConvergenceTrace ?? Array.Empty<ConvergencePoint>(),
        PassSummaries: r.Passes ?? Array.Empty<PassSummary>());

    public static ComparisonReport Build(IReadOnlyList<EvalReport> reports, string dateUtc)
    {
        var corpora = reports.Select(r => r.Info.Corpus).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        return new ComparisonReport(
            DateUtc: dateUtc,
            Corpus: string.Join("+", corpora),
            ResourceNote: EvalResults.ResourceNote,
            Models: reports.Select(FromReport)
                .OrderBy(m => m.Model, StringComparer.Ordinal).ToArray());
    }

    public static string ToJson(ComparisonReport report)
        => JsonSerializer.Serialize(report, EvalResults.JsonOpts);
}
