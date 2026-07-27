using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CleanupLatencyBench;

/// <summary>Per-statement outcome across all passes. <see cref="InputText"/>,
/// <see cref="OutputRuns"/> and <see cref="Error"/> carry transcript/exception
/// text and appear in results.json ONLY -- ToMarkdown never renders them.
/// A non-null <see cref="Error"/> is a per-statement failure row.</summary>
public sealed record StatementResult(
    string Id,
    int CharCount,
    int WordCount,
    string InputText,
    IReadOnlyList<long> CallMsRuns,      // wall ms around runner.RunAsync, per pass
    IReadOnlyList<long> ElapsedMsRuns,   // runner-reported CleanupResult.Elapsed, per pass
    IReadOnlyList<string> PathRuns,      // CleanupPath name, per pass
    IReadOnlyList<string> OutputRuns,    // cleaned output text, per pass (results.json only)
    string? Error = null);

/// <summary>Run-level facts. Model load and warm are timed once, separately,
/// and never counted in per-statement samples. <see cref="PromptFormat"/> is
/// the resolved descriptor's prompt-format id so results record WHICH template
/// produced the numbers.</summary>
public sealed record BenchRunInfo(
    string Model,
    string PromptFormat,
    string DateUtc,
    int Passes,
    int Seed,
    int TimeoutMs,
    long ModelLoadMs,
    long WarmMs,
    string StatementsSource);

public sealed record BenchSummary(
    int StatementCount,
    int LlmCallCount,
    long LlmCallP50Ms,
    long LlmCallP95Ms,
    double LlmCallMeanMs,
    IReadOnlyDictionary<string, int> PathCounts,
    int FailedCount);

public sealed record BenchReport(
    BenchRunInfo Info, BenchSummary Summary, IReadOnlyList<StatementResult> Statements);

/// <summary>
/// Cleanup bench aggregation and rendering. results.md deliberately contains NO
/// input/output transcript text (numbers and ids only -- safe to quote in
/// committed docs); results.json carries the full text and must stay out of git
/// (gitignored artifacts/ only). BCL-only so the same file compiles into
/// Winpepper.Cleanup.Tests. Mirrors the ASR bench's EvalResults.cs.
/// </summary>
public static class CleanupBenchResults
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Simple whitespace word count for reporting (chars/words columns).</summary>
    public static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>Same nearest-rank (ceiling) percentile as the ASR bench's EvalResults.</summary>
    public static double Percentile(IReadOnlyList<double> sortedAscending, double q)
    {
        if (sortedAscending.Count == 0) return 0;
        var idx = (int)Math.Ceiling(q * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(idx, 0, sortedAscending.Count - 1)];
    }

    /// <summary>Aggregate: latency percentiles over path=Llm calls only (bypass
    /// and fallback paths have different cost profiles and would skew the LLM
    /// latency picture), path counts over ALL calls, failure count.</summary>
    public static BenchSummary Summarize(IReadOnlyList<StatementResult> statements)
    {
        var llmCalls = statements
            .SelectMany(s => s.CallMsRuns.Zip(s.PathRuns, (ms, path) => (Ms: ms, Path: path)))
            .Where(c => c.Path == "Llm")
            .Select(c => (double)c.Ms)
            .OrderBy(v => v)
            .ToArray();
        var pathCounts = statements
            .SelectMany(s => s.PathRuns)
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());
        return new BenchSummary(
            StatementCount: statements.Count,
            LlmCallCount: llmCalls.Length,
            LlmCallP50Ms: (long)Percentile(llmCalls, 0.5),
            LlmCallP95Ms: (long)Percentile(llmCalls, 0.95),
            LlmCallMeanMs: llmCalls.Length == 0 ? 0 : Math.Round(llmCalls.Average(), 1),
            PathCounts: pathCounts,
            FailedCount: statements.Count(s => s.Error is not null));
    }

    public static string ToJson(BenchRunInfo info, IReadOnlyList<StatementResult> statements, BenchSummary summary)
        => JsonSerializer.Serialize(new BenchReport(info, summary, statements), JsonOpts);

    public static string ToMarkdown(BenchRunInfo info, IReadOnlyList<StatementResult> statements, BenchSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cleanup LLM latency bench");
        sb.AppendLine();
        sb.AppendLine($"- model: `{info.Model}` (prompt format: `{info.PromptFormat}`)");
        sb.AppendLine($"- date: {info.DateUtc}, passes: {info.Passes}, seed: {info.Seed}, timeout: {info.TimeoutMs} ms");
        sb.AppendLine($"- model load: {info.ModelLoadMs} ms, warm: {info.WarmMs} ms (timed once, excluded from per-statement samples)");
        sb.AppendLine($"- statements: {info.StatementsSource}");
        sb.AppendLine();
        sb.AppendLine("| statement | chars | words | call ms (passes) | path | error |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in statements)
        {
            if (s.Error is not null)
            {
                // Id, size and a marker only -- the exception text stays in results.json.
                sb.AppendLine($"| {s.Id} | {s.CharCount} | {s.WordCount} | {string.Join(" ", s.CallMsRuns)} | " +
                              $"{PathCell(s)} | ERROR |");
                continue;
            }
            sb.AppendLine($"| {s.Id} | {s.CharCount} | {s.WordCount} | {string.Join(" ", s.CallMsRuns)} | " +
                          $"{PathCell(s)} | - |");
        }
        sb.AppendLine();
        var paths = summary.PathCounts.Count == 0
            ? "(none)"
            : string.Join(", ", summary.PathCounts.Select(kv => $"{kv.Key}={kv.Value}"));
        sb.AppendLine($"**Summary:** {summary.StatementCount} statements, {info.Passes} pass(es). " +
            $"Llm calls: {summary.LlmCallCount} -- p50 {summary.LlmCallP50Ms} ms, p95 {summary.LlmCallP95Ms} ms, " +
            $"mean {summary.LlmCallMeanMs:F1} ms. Paths: {paths}. " +
            $"Model load {info.ModelLoadMs} ms, warm {info.WarmMs} ms. Failed: {summary.FailedCount}.");
        return sb.ToString();

        static string PathCell(StatementResult s) =>
            s.PathRuns.Count == 0 ? "-" : string.Join("/", s.PathRuns.Distinct());
    }
}
