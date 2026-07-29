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
        DrainMs = 42,
        TrimMs = 8,
        TrimRemovedMs = 1200,
        AsrMs = 812,
        AsrMode = "streaming",
        AsrModel = "nemotron-streaming-en",
        CorrectionsMs = 2,
        CleanupMs = 640,
        CleanupPath = "Llm",
        CleanupModel = "qwen2.5-1.5b",
        InjectMs = 850,
        InjectChars = 458,
        InjectChunksSent = 58,
        InjectChunksTotal = 58,
        InjectPacingMs = 798,
        TotalMs = 2354,
    };

    [Fact]
    public void FormatLine_FullDictation_IsOneParseableKeyValueLine()
    {
        var line = Full().FormatLine();

        line.ShouldBe(
            "session=11111111-2222-3333-4444-555555555555 kind=hold outcome=completed"
            + " rec=3512ms drain=42ms trim=8ms trim_removed=1200ms"
            + " asr=812ms asr_mode=streaming asr_model=nemotron-streaming-en"
            + " corrections=2ms cleanup=640ms cleanup_path=Llm cleanup_model=qwen2.5-1.5b"
            + " inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms"
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
            DrainMs = 30,
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
        s.DrainMs = DictationTimingSummary.DrainBudgetMs;      // 500, not over
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
}
