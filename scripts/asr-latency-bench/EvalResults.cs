using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AsrLatencyBench;

public sealed record ClipResult(
    string Id,
    double AudioSeconds,
    bool ExpectedSilent,
    bool HasReference,
    string Reference,
    string StreamText,
    string BatchText,
    double? Wer,
    double? Cer,
    bool? SilentPass,
    IReadOnlyList<long> FinishMsRuns,
    bool FellBack,          // true when ANY run fell back to batch
    int FellBackCount,      // how many of the runs fell back (0..FinishMsRuns.Count)
    bool Truncated,         // true when ANY run's native engine reported truncation
    bool TrimmedSilent,
    string BatchParityDiff,
    string? Error = null, // non-null = per-clip failure row (empty texts, null metrics); text goes to results.json only
    IReadOnlyList<long>? BatchMsRuns = null,   // whole-file batch transcribe ms per pass
    double CpuSeconds = 0,                     // total process CPU s this clip, all passes
    double MeanRtf = 0,
    bool TranscriptStable = true);             // scored transcript identical across passes

public sealed record EvalRunInfo(
    string Corpus, string SpeechModel, string TranscribeCppVersion, string DateUtc, int Repeats,
    string Mode = "streaming",                 // "streaming" | "batch"
    string? Language = null,
    int Passes = 1,
    bool Converged = false,
    string ResourceNote = EvalResults.ResourceNote);

public sealed record EvalSummary(
    int ClipCount,
    int ScoredCount,
    double? MeanWer,
    double? MedianWer,
    double? MeanCer,
    long LatencyP50Ms,
    long LatencyP90Ms,
    long LatencyMaxMs,
    int FallbackCount,
    int TruncatedCount,
    int SilentClipCount,
    int SilentPassCount,
    int FailedCount,
    double CpuSecondsTotal = 0,
    double PeakMemoryMb = 0,
    double MeanRtf = 0,
    int UnstableTranscriptCount = 0);

public sealed record EvalReport(
    EvalRunInfo Info, EvalSummary Summary, IReadOnlyList<ClipResult> Clips,
    IReadOnlyList<PassSummary>? Passes = null,
    IReadOnlyList<ConvergencePoint>? ConvergenceTrace = null);

/// <summary>
/// Corpus eval aggregation and rendering. results.md deliberately contains NO
/// transcript or reference text (safe to quote in committed docs); results.json
/// carries the full text and diffs and must stay out of git (artifacts/ only).
/// BCL-only so the same file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalResults
{
    public const string ResourceNote =
        "resources are process CPU time and peak working set only; GPU/Vulkan usage is not separately measured. " +
        "RTF = processing time / audio duration (streaming: streaming-replay process CPU seconds, batch parity decode excluded; batch: batch transcribe wall time). " +
        "Streaming-mode peak memory includes a second bench-only fallback engine (~1 extra model of RAM) that batch-only runs do not load";

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static double Percentile(IReadOnlyList<double> sortedAscending, double q)
    {
        if (sortedAscending.Count == 0) return 0;
        var idx = (int)Math.Ceiling(q * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(idx, 0, sortedAscending.Count - 1)];
    }

    public static EvalSummary Summarize(IReadOnlyList<ClipResult> clips,
        string mode = "streaming", double cpuSecondsTotal = 0, double peakMemoryMb = 0)
    {
        var wers = clips.Where(c => c.Wer is not null).Select(c => c.Wer!.Value).OrderBy(v => v).ToArray();
        var cers = clips.Where(c => c.Cer is not null).Select(c => c.Cer!.Value).ToArray();
        // 0 ms runs are silent-trimmed clips that never reached FinishAsync; exclude them.
        var latencySource = mode == "batch"
            ? clips.SelectMany(c => c.BatchMsRuns ?? Array.Empty<long>())
            : clips.SelectMany(c => c.FinishMsRuns);
        var latencies = latencySource.Where(ms => ms > 0)
            .Select(ms => (double)ms).OrderBy(v => v).ToArray();
        var rtfs = clips.Where(c => c.MeanRtf > 0).Select(c => c.MeanRtf).ToArray();
        var silent = clips.Where(c => c.ExpectedSilent).ToArray();
        return new EvalSummary(
            ClipCount: clips.Count,
            ScoredCount: wers.Length,
            MeanWer: wers.Length == 0 ? null : wers.Average(),
            MedianWer: wers.Length == 0 ? null : Percentile(wers, 0.5),
            MeanCer: cers.Length == 0 ? null : cers.Average(),
            LatencyP50Ms: (long)Percentile(latencies, 0.5),
            LatencyP90Ms: (long)Percentile(latencies, 0.9),
            LatencyMaxMs: latencies.Length == 0 ? 0 : (long)latencies[^1],
            FallbackCount: clips.Count(c => c.FellBack),
            TruncatedCount: clips.Count(c => c.Truncated),
            SilentClipCount: silent.Length,
            SilentPassCount: silent.Count(c => c.SilentPass == true),
            FailedCount: clips.Count(c => c.Error is not null),
            CpuSecondsTotal: Math.Round(cpuSecondsTotal, 3),
            PeakMemoryMb: Math.Round(peakMemoryMb, 1),
            MeanRtf: rtfs.Length == 0 ? 0 : Math.Round(rtfs.Average(), 4),
            UnstableTranscriptCount: clips.Count(c => !c.TranscriptStable));
    }

    public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary)
        => JsonSerializer.Serialize(new EvalReport(info, summary, clips), JsonOpts);

    public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary,
        IReadOnlyList<PassSummary> passes, IReadOnlyList<ConvergencePoint> convergenceTrace)
        => JsonSerializer.Serialize(new EvalReport(info, summary, clips, passes, convergenceTrace), JsonOpts);

    public static string ToMarkdown(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ASR corpus eval: {info.Corpus}");
        sb.AppendLine();
        sb.AppendLine($"- speech model: `{info.SpeechModel}`");
        sb.AppendLine($"- transcribe.cpp: `{info.TranscribeCppVersion}`");
        sb.AppendLine($"- date: {info.DateUtc}, repeats: {info.Repeats}");
        sb.AppendLine($"- mode: {info.Mode}{(info.Language is null ? "" : $" (language {info.Language})")}");
        sb.AppendLine($"- passes: {info.Passes}, converged: {(info.Converged ? "yes" : "no")}");
        sb.AppendLine($"- CPU: {summary.CpuSecondsTotal:F1} s total, peak memory: {summary.PeakMemoryMb:F0} MB, mean RTF: {summary.MeanRtf:F3}");
        sb.AppendLine();
        sb.AppendLine("| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack (runs) | truncated | error |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var c in clips)
        {
            if (c.Error is not null)
            {
                // Ids and a marker only -- the exception text stays in results.json.
                sb.AppendLine($"| {c.Id} | - | - | - | - | - | - | - | ERROR |");
                continue;
            }
            var werCell = c.Wer is not null ? c.Wer.Value.ToString("F3") : (c.ExpectedSilent ? "-" : "no ref");
            var cerCell = c.Cer is not null ? c.Cer.Value.ToString("F3") : "-";
            var silentCell = c.SilentPass is null ? "-" : (c.SilentPass.Value ? "PASS" : "FAIL");
            sb.AppendLine($"| {c.Id} | {c.AudioSeconds:F1} | {werCell} | {cerCell} | {silentCell} | " +
                          $"{string.Join(" ", c.FinishMsRuns)} | {c.FellBackCount}/{c.FinishMsRuns.Count} | {c.Truncated} | - |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Summary:** {summary.ClipCount} clips ({summary.ScoredCount} scored). " +
            $"WER mean {Fmt(summary.MeanWer)} / median {Fmt(summary.MedianWer)}; CER mean {Fmt(summary.MeanCer)}. " +
            $"Post-stop latency p50 {summary.LatencyP50Ms} ms, p90 {summary.LatencyP90Ms} ms, max {summary.LatencyMaxMs} ms. " +
            $"Fallbacks: {summary.FallbackCount}. Truncations: {summary.TruncatedCount}. " +
            $"Silent clips: {summary.SilentPassCount}/{summary.SilentClipCount} pass. " +
            $"Failed: {summary.FailedCount}.");
        return sb.ToString();

        static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F3");
    }
}
