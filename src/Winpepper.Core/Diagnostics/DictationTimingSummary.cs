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
    public const int MicStopBudgetMs = 500;       // provisional: mic buffer copy + tee teardown (WarmRecorder.StopSession)
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
    public const int AsrWaitBudgetMs = 500;       // asr_wait: _pump.WaitAsync after stop. >500 ms means EITHER
                                                  // native feeds ran slower than real time during recording
                                                  // (frames backed up in the unbounded channel; backlog_ms will
                                                  // be large) OR the session was still starting at stop (cold
                                                  // factory/model load, cloud connect; backlog_ms small, load
                                                  // INFs nearby). The WRN is a flag to look, not a verdict.
    public const int TotalBudgetMs = 5000;        // provisional: beyond this it "felt slow" by definition

    public required Guid SessionId { get; init; }
    public required string Kind { get; init; }          // "hold" | "toggle"
    public string Outcome { get; set; } = "completed";  // completed|pending|silent|failed|empty

    public int? RecordMs { get; set; }
    public int? MicStopMs { get; set; }
    public int? TrimMs { get; set; }
    public int? TrimRemovedMs { get; set; }
    public int? AsrMs { get; set; }
    public string? AsrMode { get; set; }                // "streaming" | "batch" | "cloud"
    public string? AsrModel { get; set; }
    public int? AsrWaitMs { get; set; }         // FinishAsync: _pump.WaitAsync backlog drain
    public int? AsrNativeMs { get; set; }       // FinishAsync: inner session finish (tail feed + finalize)
    public int? BacklogFrames { get; set; }     // frames queued but not yet pumped at finish entry
    public int? BacklogMs { get; set; }         // queued samples / 16 (16 kHz mono)
    public int? NativeCalls { get; set; }       // per-session native call aggregates (NativeCallStats)
    public int? NativeTotalMs { get; set; }
    public int? NativeMaxMs { get; set; }
    public int? NativeOver250 { get; set; }
    public int? CorrectionsMs { get; set; }
    public int? CleanupMs { get; set; }
    public string? CleanupPath { get; set; }            // CleanupPath enum name or "exception"
    public string? CleanupModel { get; set; }
    public int? InjectMs { get; set; }
    public int? InjectChars { get; set; }
    public int? InjectChunksSent { get; set; }
    public int? InjectChunksTotal { get; set; }
    public int? InjectPacingMs { get; set; }
    public int? GcGen0 { get; set; }            // GC.CollectionCount deltas, recording start -> emit
    public int? GcGen1 { get; set; }
    public int? GcGen2 { get; set; }
    public int? GcPauseMs { get; set; }         // GC.GetTotalPauseDuration() delta, recording start -> emit:
                                                // actual GC pause TIME (counts can't convey magnitude)
    public bool? PrewarmActive { get; set; }    // cleanup pre-warm overlapped this dictation
    public IReadOnlyList<int>? Over250AtMs { get; set; }   // 0b: ms offsets from recording start of native calls >= 250 ms; capped upstream at 16 entries
    public int? Over250Overflow { get; set; }              // 0b: over-250 events beyond the 16-entry cap
    public string? CtxSrc { get; set; }                    // 0b: window context the cleanup LLM ACTUALLY consumed: uia|ocr|none (consume-time semantics)
    public int? ProcCpuMs { get; set; }                    // 0b: Process.TotalProcessorTime delta, recording start -> StopRequested (NOT emit)
    public int? PageFaults { get; set; }   // B1: page-fault count delta, recording start -> StopRequested
    public int? MemPrivMb { get; set; }    // B2: private bytes MB, sampled once at recording start
    public int? MemWsMb { get; set; }      // B2: working set MB, sampled once at recording start
    public int? ThreadCount { get; set; }  // B2: process thread count at recording start
    public int? HandleCount { get; set; }  // B2: process handle count at recording start
    public int? SysCpuPct { get; set; }    // B3: system-wide CPU % over the recording window (GetSystemTimes delta)
    public bool? CpuPegged { get; set; }   // pegged decision near recording start (what the pill showed); null = no reading, field omitted
    /// <summary>Warm pre-roll ms the recorder ACTUALLY seeded into this session
    /// (StartSession's return; 0 in cold mode). Head-loss diagnostics, 2026-08-04.</summary>
    public int? PrerollMs { get; set; }

    /// <summary>Hotkey-keydown (hook timestamp) -> pre-roll-seed lag in ms,
    /// measured immediately before StartSession; >= the 'Session started' line's
    /// LagMs (blocking in-arm work sits between the two). Uncompensated, this
    /// eats pre-keydown coverage 1:1 (M2); see PrerollRequest.</summary>
    public int? ArmLatencyMs { get; set; }

    /// <summary>ms between the previous session's stop hotkey and this session's
    /// start hotkey; assigned only when 0 &lt;= gap &lt; 3000 (the retrigger
    /// signature) — the filter lives at the assignment site.</summary>
    public int? RetriggerGapMs { get; set; }

    /// <summary>ms offset (buffer t=0) of the first clear-speech frame outside the
    /// cue-pickup window (SilenceTrimmer TrimResult.HeadSpeechAtMs); null when none
    /// or when trim never ran.</summary>
    public int? HeadSpeechAtMs { get; set; }

    /// <summary>True when head speech lands in the first two 20 ms frames — speech
    /// predating the recording window. Null when HeadSpeechAtMs is null.</summary>
    public bool? HeadClipped { get; set; }
    public int? TotalMs { get; set; }                   // hotkey-release -> emit, wall clock

    public string FormatLine()
    {
        var sb = new StringBuilder(256);
        sb.Append("session=").Append(SessionId);
        sb.Append(" kind=").Append(Kind);
        sb.Append(" outcome=").Append(Outcome);
        AppendCoreMs(sb, "rec", RecordMs);
        AppendOptMs(sb, "preroll", PrerollMs);
        AppendOptMs(sb, "arm_latency", ArmLatencyMs);
        AppendOptMs(sb, "retrigger_gap", RetriggerGapMs);
        AppendOptMs(sb, "head_speech_at", HeadSpeechAtMs);
        if (HeadClipped is bool clipped)
            sb.Append(" head_clipped=").Append(clipped ? "true" : "false");
        AppendCoreMs(sb, "mic_stop", MicStopMs);
        AppendCoreMs(sb, "trim", TrimMs);
        AppendOptMs(sb, "trim_removed", TrimRemovedMs);
        AppendCoreMs(sb, "asr", AsrMs);
        AppendOptStr(sb, "asr_mode", AsrMode);
        AppendOptStr(sb, "asr_model", AsrModel);
        AppendOptMs(sb, "asr_wait", AsrWaitMs);
        AppendOptMs(sb, "asr_native", AsrNativeMs);
        AppendOptNum(sb, "backlog", BacklogFrames);
        AppendOptMs(sb, "backlog_ms", BacklogMs);
        AppendOptNum(sb, "native_calls", NativeCalls);
        AppendOptMs(sb, "native_total", NativeTotalMs);
        AppendOptMs(sb, "native_max", NativeMaxMs);
        AppendOptNum(sb, "native_over250", NativeOver250);
        if (Over250AtMs is { Count: > 0 } over250)
        {
            sb.Append(" over250_at=[");
            for (var i = 0; i < over250.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(over250[i]);
            }
            sb.Append(']');
            if (Over250Overflow is int extra and > 0) sb.Append('+').Append(extra);
        }
        AppendCoreMs(sb, "corrections", CorrectionsMs);
        AppendCoreMs(sb, "cleanup", CleanupMs);
        AppendOptStr(sb, "cleanup_path", CleanupPath);
        AppendOptStr(sb, "cleanup_model", CleanupModel);
        AppendOptStr(sb, "ctx_src", CtxSrc);
        AppendCoreMs(sb, "inject", InjectMs);
        AppendOptNum(sb, "inject_chars", InjectChars);
        if (InjectChunksSent is not null || InjectChunksTotal is not null)
            sb.Append(" inject_chunks=").Append(InjectChunksSent ?? 0).Append('/').Append(InjectChunksTotal ?? 0);
        AppendOptMs(sb, "inject_pace", InjectPacingMs);
        if (GcGen0 is not null || GcGen1 is not null || GcGen2 is not null)
            sb.Append(" gc=").Append(GcGen0 ?? 0).Append('/').Append(GcGen1 ?? 0).Append('/').Append(GcGen2 ?? 0);
        AppendOptMs(sb, "gc_pause", GcPauseMs);
        if (PrewarmActive is bool prewarm)
            sb.Append(" prewarm_active=").Append(prewarm ? "true" : "false");
        AppendOptNum(sb, "proc_cpu_ms", ProcCpuMs);
        AppendOptNum(sb, "pf", PageFaults);
        if (MemPrivMb is not null || MemWsMb is not null)
            sb.Append(" mem=").Append(MemPrivMb ?? 0).Append('/').Append(MemWsMb ?? 0);
        AppendOptNum(sb, "thr", ThreadCount);
        AppendOptNum(sb, "hnd", HandleCount);
        AppendOptNum(sb, "sys_cpu", SysCpuPct);
        if (CpuPegged is bool pegged)
            sb.Append(" cpu_pegged=").Append(pegged ? "true" : "false");
        AppendCoreMs(sb, "total", TotalMs);
        return sb.ToString();
    }

    /// <summary>0b: convert absolute <see cref="Environment.TickCount64"/> stamps of
    /// slow native calls into ms offsets from recording start. Offsets are UNCLAMPED
    /// on purpose — values after the stop request are themselves evidence.</summary>
    public void StampOver250(IReadOnlyList<long> startTicks, int overflowCount, long recordingStartTicks)
    {
        var offsets = new int[startTicks.Count];
        for (var i = 0; i < startTicks.Count; i++)
            offsets[i] = (int)(startTicks[i] - recordingStartTicks);
        Over250AtMs = offsets;
        Over250Overflow = overflowCount;
    }

    /// <summary>B3: system-wide CPU percent over the recording window, from two
    /// GetSystemTimes samples (100 ns FILETIME units). Windows' kernel time
    /// INCLUDES idle time (doc-confirmed 2026-07-30: "This time value also
    /// includes the amount of time the system has been idle."), so
    /// busy = (kernel - idle) + user. Null when the window is empty or
    /// inconsistent (first sample failed, clock skew).</summary>
    public static int? SystemCpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;
        if (total <= 0) return null;
        var busy = kernelDelta - idleDelta + userDelta;
        if (busy < 0) return null;
        return (int)(busy * 100 / total);
    }

    public IReadOnlyList<StageOverrun> Overruns()
    {
        var list = new List<StageOverrun>(2);
        Check(list, "mic_stop", MicStopMs, MicStopBudgetMs);
        Check(list, "trim", TrimMs, TrimBudgetMs);
        // Per-mode asr budget (the WRN's budget figure also reveals the
        // mode). ONLY an explicit "streaming" gets the tight budget; batch,
        // cloud (measured p50 2247 ms), and unknown/null modes all use the
        // generous batch budget so a misclassification fails toward silence.
        Check(list, "asr", AsrMs, AsrMode == "streaming" ? AsrStreamingBudgetMs : AsrBatchBudgetMs);
        Check(list, "asr_wait", AsrWaitMs, AsrWaitBudgetMs);
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
