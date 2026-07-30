using System;
using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class DictationTimingSummaryTests
{
    private static DictationTimingSummary Full() => new()
    {
        SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Kind = "hold",
        Outcome = "completed",
        RecordMs = 3512,
        MicStopMs = 42,
        TrimMs = 8,
        TrimRemovedMs = 1200,
        AsrMs = 812,
        AsrMode = "streaming",
        AsrModel = "nemotron-streaming-en",
        AsrWaitMs = 95,
        AsrNativeMs = 210,
        BacklogFrames = 2,
        BacklogMs = 100,
        NativeCalls = 74,
        NativeTotalMs = 1900,
        // Over-250 diagnostics — keep the fixture self-consistent: max and the
        // over-250 count must agree with the offsets list.
        NativeMaxMs = 620,
        NativeOver250 = 2,
        Over250AtMs = new[] { 1180, 3420 },
        Over250Overflow = 0,
        CtxSrc = "uia",
        ProcCpuMs = 1875,
        PageFaults = 418,
        MemPrivMb = 3061,
        MemWsMb = 1542,
        ThreadCount = 167,
        HandleCount = 2003,
        SysCpuPct = 37,
        CorrectionsMs = 2,
        CleanupMs = 640,
        CleanupPath = "Llm",
        CleanupModel = "qwen2.5-1.5b",
        InjectMs = 850,
        InjectChars = 458,
        InjectChunksSent = 58,
        InjectChunksTotal = 58,
        InjectPacingMs = 798,
        GcGen0 = 1,
        GcGen1 = 0,
        GcGen2 = 0,
        GcPauseMs = 12,
        PrewarmActive = true,
        TotalMs = 2354,
    };

    [Fact]
    public void FormatLine_FullDictation_IsOneParseableKeyValueLine()
    {
        var line = Full().FormatLine();

        line.ShouldBe(
            "session=11111111-2222-3333-4444-555555555555 kind=hold outcome=completed"
            + " rec=3512ms mic_stop=42ms trim=8ms trim_removed=1200ms"
            + " asr=812ms asr_mode=streaming asr_model=nemotron-streaming-en"
            + " asr_wait=95ms asr_native=210ms backlog=2 backlog_ms=100ms"
            + " native_calls=74 native_total=1900ms native_max=620ms native_over250=2 over250_at=[1180,3420]"
            + " corrections=2ms cleanup=640ms cleanup_path=Llm cleanup_model=qwen2.5-1.5b ctx_src=uia"
            + " inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms"
            + " gc=1/0/0 gc_pause=12ms prewarm_active=true proc_cpu_ms=1875"
            + " pf=418 mem=3061/1542 thr=167 hnd=2003 sys_cpu=37"
            + " total=2354ms");
        line.ShouldNotContain("\n");
    }

    [Fact]
    public void FormatLine_SilentDrop_MarksSkippedStages()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "toggle",
            Outcome = "silent",
            RecordMs = 8950,
            MicStopMs = 30,
            TrimMs = 12,
            TotalMs = 60,
        };

        var line = s.FormatLine();

        line.ShouldContain("kind=toggle");
        line.ShouldContain("outcome=silent");
        line.ShouldContain("asr=skip");
        line.ShouldContain("corrections=skip");
        line.ShouldContain("cleanup=skip");
        line.ShouldContain("inject=skip");
        // Optional extras are omitted entirely when unknown, not "skip"-ed.
        line.ShouldNotContain("trim_removed");
        line.ShouldNotContain("asr_model");
        line.ShouldNotContain("inject_chars");
        line.ShouldNotContain("inject_chunks");
    }

    [Fact]
    public void FormatLine_QuotesStringValuesContainingSpaces()
    {
        var s = Full();
        s.CleanupModel = "none (cloud, corrections-only)";

        s.FormatLine().ShouldContain("cleanup_model=\"none (cloud, corrections-only)\"");
    }

    [Fact]
    public void Overruns_AtBudget_IsEmpty()
    {
        var s = Full();
        s.MicStopMs = DictationTimingSummary.MicStopBudgetMs;   // 500, not over
        s.TrimMs = DictationTimingSummary.TrimBudgetMs;        // 200
        s.AsrMs = DictationTimingSummary.AsrStreamingBudgetMs; // 2000 (Full() is streaming)
        s.CorrectionsMs = DictationTimingSummary.CorrectionsBudgetMs;
        s.CleanupMs = DictationTimingSummary.CleanupBudgetMs;
        s.InjectMs = DictationTimingSummary.InjectBudgetMs;
        s.TotalMs = DictationTimingSummary.TotalBudgetMs;

        s.Overruns().ShouldBeEmpty();
    }

    [Fact]
    public void Overruns_OverBudget_NamesStageActualAndBudget()
    {
        var s = Full();
        s.AsrMs = 2001;
        s.TotalMs = 5001;

        var overruns = s.Overruns();

        overruns.ShouldBe(new[]
        {
            new StageOverrun("asr", 2001, 2000),
            new StageOverrun("total", 5001, 5000),
        });
    }

    [Fact]
    public void Overruns_BatchAsr_UsesBatchBudget()
    {
        // Budgets are per-mode: healthy batch ASR measured p50 3.2-3.6 s /
        // p90 6.0-7.0 s -- a flat 2000 ms budget would WRN on 84-86% of
        // batch dictations.
        var s = Full();
        s.AsrMode = "batch";
        s.AsrMs = 3500; // routine healthy batch -- must NOT warn

        s.Overruns().ShouldBeEmpty();

        s.AsrMs = DictationTimingSummary.AsrBatchBudgetMs + 1;

        s.Overruns().ShouldBe(new[]
        {
            new StageOverrun("asr", DictationTimingSummary.AsrBatchBudgetMs + 1,
                DictationTimingSummary.AsrBatchBudgetMs),
        });
    }

    [Fact]
    public void Overruns_CloudAsr_UsesBatchBudget()
    {
        // "cloud" is a distinct mode for truthful logging but shares the
        // batch budget: measured cloud (AssemblyAI) p50 is ~2247 ms, which
        // must NOT trip the 2000 ms streaming budget. Only an explicit
        // "streaming" mode gets the tight budget -- unknown modes fail
        // toward silence.
        var s = Full();
        s.AsrMode = "cloud";
        s.AsrMs = 2247; // routine healthy cloud -- must NOT warn

        s.Overruns().ShouldBeEmpty();

        s.AsrMs = DictationTimingSummary.AsrBatchBudgetMs + 1;

        s.Overruns().ShouldBe(new[]
        {
            new StageOverrun("asr", DictationTimingSummary.AsrBatchBudgetMs + 1,
                DictationTimingSummary.AsrBatchBudgetMs),
        });
    }

    [Fact]
    public void Overruns_SkippedStages_ProduceNoWarnings()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.NewGuid(),
            Kind = "hold",
            Outcome = "silent",
        };

        s.Overruns().ShouldBeEmpty();
    }

    [Fact]
    public void Overruns_RecordingHasNoBudget()
    {
        var s = Full();
        s.RecordMs = 600_000; // a 10-minute recording is the user's business

        s.Overruns().ShouldBeEmpty();
    }

    [Fact]
    public void FormatLine_NewDiagnosticFields_AreOmittedWhenNull()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "hold",
        };

        var line = s.FormatLine();

        line.ShouldNotContain("asr_wait=");
        line.ShouldNotContain("asr_native=");
        line.ShouldNotContain("backlog");
        line.ShouldNotContain("native_");
        line.ShouldNotContain("gc=");
        line.ShouldNotContain("gc_pause=");
        line.ShouldNotContain("prewarm_active=");
        line.ShouldNotContain("over250_at=");
        line.ShouldNotContain("ctx_src=");
        line.ShouldNotContain("proc_cpu_ms=");
    }

    [Fact]
    public void FormatLine_GcTriple_RendersWhenAnyGenIsSet()
    {
        var s = Full();
        s.GcGen0 = 3;
        s.GcGen1 = null;
        s.GcGen2 = null;

        s.FormatLine().ShouldContain("gc=3/0/0");
    }

    [Fact]
    public void Overruns_AsrWaitOverBudget_Warns()
    {
        var s = Full();
        s.AsrWaitMs = DictationTimingSummary.AsrWaitBudgetMs + 1;

        s.Overruns().ShouldContain(new StageOverrun(
            "asr_wait", DictationTimingSummary.AsrWaitBudgetMs + 1, DictationTimingSummary.AsrWaitBudgetMs));
    }

    [Fact]
    public void Overruns_AsrWaitAtBudget_IsClean()
    {
        var s = Full();
        s.AsrWaitMs = DictationTimingSummary.AsrWaitBudgetMs;

        s.Overruns().ShouldNotContain(o => o.Stage == "asr_wait");
    }

    [Fact]
    public void FormatLine_Over250_RendersOverflowSuffix_OnlyWhenPositive()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.Over250AtMs = new[] { 300, 5100 };
        t.Over250Overflow = 3;
        t.FormatLine().ShouldContain(" over250_at=[300,5100]+3");

        t.Over250Overflow = 0;
        t.FormatLine().ShouldContain(" over250_at=[300,5100]");
        t.FormatLine().ShouldNotContain("]+");
    }

    [Fact]
    public void FormatLine_Over250_EmptyList_IsOmitted()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.Over250AtMs = Array.Empty<int>();
        t.Over250Overflow = 0;
        t.FormatLine().ShouldNotContain("over250_at=");
    }

    [Fact]
    public void StampOver250_ConvertsTicksToOffsets_Unclamped()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        // Recording started at tick 10_000. Third entry is AFTER the stop
        // request — offsets are unclamped on purpose (post-stop offsets are
        // themselves evidence, per the approved plan's 0b).
        t.StampOver250(new long[] { 10_300, 12_000, 19_999 }, overflowCount: 1, recordingStartTicks: 10_000);
        t.Over250AtMs.ShouldBe(new[] { 300, 2000, 9999 });
        t.Over250Overflow.ShouldBe(1);
        t.FormatLine().ShouldContain(" over250_at=[300,2000,9999]+1");
    }

    [Fact]
    public void FormatLine_CtxSrcAndProcCpu_RenderAsPlainKeyValues()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.CtxSrc = "none";
        t.ProcCpuMs = 42;
        var line = t.FormatLine();
        line.ShouldContain(" ctx_src=none");
        line.ShouldContain(" proc_cpu_ms=42");
    }

    [Fact]
    public void FormatLine_ResourceFields_RenderAsPlainKeyValues()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.PageFaults = 418;
        t.MemPrivMb = 3061;
        t.MemWsMb = 1542;
        t.ThreadCount = 167;
        t.HandleCount = 2003;
        t.SysCpuPct = 37;
        var line = t.FormatLine();
        line.ShouldContain(" pf=418");
        line.ShouldContain(" mem=3061/1542");
        line.ShouldContain(" thr=167");
        line.ShouldContain(" hnd=2003");
        line.ShouldContain(" sys_cpu=37");
    }

    [Fact]
    public void FormatLine_ResourceFields_OmittedWhenNull()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        var line = t.FormatLine();
        line.ShouldNotContain(" pf=");
        line.ShouldNotContain(" mem=");
        line.ShouldNotContain(" thr=");
        line.ShouldNotContain(" hnd=");
        line.ShouldNotContain(" sys_cpu=");
    }

    [Theory]
    [InlineData(900, 1000, 0, 10)]   // busy = (1000-900)+0 = 100 of 1000
    [InlineData(0, 500, 500, 100)]   // fully busy
    [InlineData(1000, 1000, 0, 0)]   // fully idle
    public void SystemCpuPercent_ComputesBusyShareOfTotal(long idle, long kernel, long user, int expected)
    {
        DictationTimingSummary.SystemCpuPercent(idle, kernel, user).ShouldBe(expected);
    }

    [Fact]
    public void SystemCpuPercent_InvalidWindow_ReturnsNull()
    {
        DictationTimingSummary.SystemCpuPercent(0, 0, 0).ShouldBeNull();      // empty window
        DictationTimingSummary.SystemCpuPercent(2000, 1000, 0).ShouldBeNull(); // busy < 0 (clock skew)
    }
}
