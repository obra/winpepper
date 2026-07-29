using System.Text;

namespace Winpepper.Core.Diagnostics;

/// <summary>One stage exceeding its budget; rendered as a [WRN] log line.</summary>
public readonly record struct StageOverrun(string Stage, int ActualMs, int BudgetMs);

/// <summary>
/// Per-dictation timing accumulator + formatter. PipelineHost creates one
/// per dictation, stamps stage durations along the existing flow (Stopwatch
/// reads only -- no threads, no timers, no hot-path allocations), and emits
/// FormatLine() as ONE structured [INF] line at the end, so "where did the
/// 3 s go?" is answerable from the log alone, after the fact. Core stages
/// left null render as "skip" (the summary appears even when stages are
/// skipped); optional detail fields left null are omitted. Overruns()
/// classifies stages against fixed budgets for grep-able [WRN] lines.
/// Pure and Linux-testable by design (DictationTimingSummaryTests).
/// </summary>
public sealed class DictationTimingSummary
{
    // Stage budgets (ms). Recording has none: its duration is the user's.
    // cleanup and the two asr budgets are log-derived (production
    // distributions, re-verified against the raw logs 2026-07-28); the rest
    // are PROVISIONAL -- re-derive from the first weeks of `dictation
    // timing` lines. The cleanup live-swap merged AFTER the cleanup
    // measurement window, so recheck that distribution in week one too.
    public const int DrainBudgetMs = 500;         // provisional: buffer copy + tee teardown
    public const int TrimBudgetMs = 200;          // provisional: pure math over <=60 s of floats
    public const int AsrStreamingBudgetMs = 2000; // measured: streaming p90 <= 464 ms on measured days
                                                  // (day variance is real: 07-26 p90 = 1640 ms, still under)
    public const int AsrBatchBudgetMs = 8000;     // measured: healthy batch p50 3.2-3.6 s, p90 6-7 s;
                                                  // cloud (n=30, p50 2247 ms) shares this budget
    public const int CorrectionsBudgetMs = 150;   // provisional: local file load
    public const int CleanupBudgetMs = 2000;      // measured: Llm path n=453 p50=505 p90=808, 1 overrun
                                                  // (window 2026-07-17..28)
    public const int InjectBudgetMs = 1500;       // provisional: ~0.8 s nominal send for 458 chars at
                                                  // the 14 ms/8-unit deadline pace; a full 1500 ms
                                                  // release-wait prelude overruns -- deserving a WRN
    public const int TotalBudgetMs = 5000;        // provisional: beyond this it "felt slow" by definition

    public required Guid SessionId { get; init; }
    public required string Kind { get; init; }          // "hold" | "toggle"
    public string Outcome { get; set; } = "completed";  // completed|pending|silent|failed|empty

    public int? RecordMs { get; set; }
    public int? DrainMs { get; set; }
    public int? TrimMs { get; set; }
    public int? TrimRemovedMs { get; set; }
    public int? AsrMs { get; set; }
    public string? AsrMode { get; set; }                // "streaming" | "batch" | "cloud"
    public string? AsrModel { get; set; }
    public int? CorrectionsMs { get; set; }
    public int? CleanupMs { get; set; }
    public string? CleanupPath { get; set; }            // CleanupPath enum name or "exception"
    public string? CleanupModel { get; set; }
    public int? InjectMs { get; set; }
    public int? InjectChars { get; set; }
    public int? InjectChunksSent { get; set; }
    public int? InjectChunksTotal { get; set; }
    public int? InjectPacingMs { get; set; }
    public int? TotalMs { get; set; }                   // hotkey-release -> emit, wall clock

    public string FormatLine()
    {
        var sb = new StringBuilder(256);
        sb.Append("session=").Append(SessionId);
        sb.Append(" kind=").Append(Kind);
        sb.Append(" outcome=").Append(Outcome);
        AppendCoreMs(sb, "rec", RecordMs);
        AppendCoreMs(sb, "drain", DrainMs);
        AppendCoreMs(sb, "trim", TrimMs);
        AppendOptMs(sb, "trim_removed", TrimRemovedMs);
        AppendCoreMs(sb, "asr", AsrMs);
        AppendOptStr(sb, "asr_mode", AsrMode);
        AppendOptStr(sb, "asr_model", AsrModel);
        AppendCoreMs(sb, "corrections", CorrectionsMs);
        AppendCoreMs(sb, "cleanup", CleanupMs);
        AppendOptStr(sb, "cleanup_path", CleanupPath);
        AppendOptStr(sb, "cleanup_model", CleanupModel);
        AppendCoreMs(sb, "inject", InjectMs);
        AppendOptNum(sb, "inject_chars", InjectChars);
        if (InjectChunksSent is not null || InjectChunksTotal is not null)
            sb.Append(" inject_chunks=").Append(InjectChunksSent ?? 0).Append('/').Append(InjectChunksTotal ?? 0);
        AppendOptMs(sb, "inject_pace", InjectPacingMs);
        AppendCoreMs(sb, "total", TotalMs);
        return sb.ToString();
    }

    public IReadOnlyList<StageOverrun> Overruns()
    {
        var list = new List<StageOverrun>(2);
        Check(list, "drain", DrainMs, DrainBudgetMs);
        Check(list, "trim", TrimMs, TrimBudgetMs);
        // Per-mode asr budget (the WRN's budget figure also reveals the
        // mode). ONLY an explicit "streaming" gets the tight budget; batch,
        // cloud (measured p50 2247 ms), and unknown/null modes all use the
        // generous batch budget so a misclassification fails toward silence.
        Check(list, "asr", AsrMs, AsrMode == "streaming" ? AsrStreamingBudgetMs : AsrBatchBudgetMs);
        Check(list, "corrections", CorrectionsMs, CorrectionsBudgetMs);
        Check(list, "cleanup", CleanupMs, CleanupBudgetMs);
        Check(list, "inject", InjectMs, InjectBudgetMs);
        Check(list, "total", TotalMs, TotalBudgetMs);
        return list;
    }

    private static void Check(List<StageOverrun> list, string stage, int? actual, int budget)
    {
        if (actual is int a && a > budget) list.Add(new StageOverrun(stage, a, budget));
    }

    private static void AppendCoreMs(StringBuilder sb, string key, int? value)
    {
        sb.Append(' ').Append(key).Append('=');
        if (value is int v) sb.Append(v).Append("ms");
        else sb.Append("skip");
    }

    private static void AppendOptMs(StringBuilder sb, string key, int? value)
    {
        if (value is int v) sb.Append(' ').Append(key).Append('=').Append(v).Append("ms");
    }

    private static void AppendOptNum(StringBuilder sb, string key, int? value)
    {
        if (value is int v) sb.Append(' ').Append(key).Append('=').Append(v);
    }

    private static void AppendOptStr(StringBuilder sb, string key, string? value)
    {
        if (value is null) return;
        sb.Append(' ').Append(key).Append('=');
        if (value.Contains(' ')) sb.Append('"').Append(value).Append('"');
        else sb.Append(value);
    }
}
