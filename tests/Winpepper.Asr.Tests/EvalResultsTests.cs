using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalResultsTests
{
    private static ClipResult Clip(
        string id, double? wer = null, double? cer = null, bool? silentPass = null,
        bool expectedSilent = false, long[]? runsMs = null, bool fellBack = false,
        int? fellBackCount = null, bool truncated = false) =>
        new(id, 3.0, expectedSilent, HasReference: wer is not null,
            Reference: "secret reference words", StreamText: "secret stream words", BatchText: "secret batch words",
            wer, cer, silentPass, runsMs ?? new long[] { 100 }, fellBack,
            fellBackCount ?? (fellBack ? 1 : 0), truncated,
            TrimmedSilent: false, BatchParityDiff: "IDENTICAL");

    private static readonly EvalRunInfo Info = new("corpus-v1", "model-x", "0.1.3", "2026-07-26", 1);

    [Fact]
    public void Summarize_ComputesMeansMediansPercentilesAndCounts()
    {
        var clips = new[]
        {
            Clip("a", wer: 0.10, cer: 0.05, runsMs: new long[] { 100, 200 }),
            Clip("b", wer: 0.30, cer: 0.15, runsMs: new long[] { 300, 400 }, fellBack: true, truncated: true),
            Clip("c", silentPass: true, expectedSilent: true, runsMs: new long[] { 0 }),
        };

        var s = EvalResults.Summarize(clips);

        s.ClipCount.ShouldBe(3);
        s.ScoredCount.ShouldBe(2);
        s.MeanWer!.Value.ShouldBe(0.20, tolerance: 1e-9);
        s.MedianWer!.Value.ShouldBe(0.10, tolerance: 1e-9);
        s.MeanCer!.Value.ShouldBe(0.10, tolerance: 1e-9);
        s.LatencyP50Ms.ShouldBe(200);    // 0 ms silent-skip runs are excluded
        s.LatencyMaxMs.ShouldBe(400);
        s.FallbackCount.ShouldBe(1);
        s.TruncatedCount.ShouldBe(1);
        s.SilentClipCount.ShouldBe(1);
        s.SilentPassCount.ShouldBe(1);
    }

    [Fact]
    public void Summarize_NoScoredClips_YieldsNullRates()
    {
        var s = EvalResults.Summarize(new[] { Clip("a") });

        s.MeanWer.ShouldBeNull();
        s.MedianWer.ShouldBeNull();
        s.MeanCer.ShouldBeNull();
    }

    [Fact]
    public void ToMarkdown_HasPerClipRowsAndSummary_ButNoTranscriptText()
    {
        var clips = new[] { Clip("clip1", wer: 0.25, cer: 0.10) };

        var md = EvalResults.ToMarkdown(Info, clips, EvalResults.Summarize(clips));

        md.ShouldContain("corpus-v1");
        md.ShouldContain("model-x");
        md.ShouldContain("0.1.3");
        md.ShouldContain("| clip1 |");
        md.ShouldContain("0.250");
        md.ShouldContain("**Summary:**");
        md.ShouldNotContain("secret");   // transcripts/references never leak into markdown
    }

    [Fact]
    public void ToJson_CarriesFullTranscriptsAndRoundTripFields()
    {
        var clips = new[] { Clip("clip1", wer: 0.25) };

        var json = EvalResults.ToJson(Info, clips, EvalResults.Summarize(clips));

        json.ShouldContain("\"corpus\": \"corpus-v1\"");
        json.ShouldContain("\"secret reference words\"");
        json.ShouldContain("\"wer\": 0.25");
        json.ShouldContain("\"finishMsRuns\"");
    }

    [Fact]
    public void PartialFallback_ReportsFlagAndPerRunCount()
    {
        // A clip that fell back on 2 of its 3 runs: FellBack is true (ANY run),
        // the count says how many, markdown shows "count/runs", JSON carries both.
        var clips = new[]
        {
            Clip("partial", wer: 0.10, runsMs: new long[] { 100, 200, 300 },
                fellBack: true, fellBackCount: 2),
            Clip("clean", wer: 0.20, runsMs: new long[] { 100, 200, 300 }),
        };

        var s = EvalResults.Summarize(clips);
        s.FallbackCount.ShouldBe(1); // clips with ANY fallback, not run count

        var md = EvalResults.ToMarkdown(Info, clips, s);
        md.ShouldContain("| 2/3 |");
        md.ShouldContain("| 0/3 |");

        var json = EvalResults.ToJson(Info, clips, s);
        json.ShouldContain("\"fellBack\": true");
        json.ShouldContain("\"fellBackCount\": 2");
    }

    [Fact]
    public void FailedClip_CountedInSummary_MarkedInMarkdownWithoutErrorText()
    {
        // Error rows (per-clip failures in the corpus run) have empty texts and
        // null metrics; results.md shows only an ERROR marker + counts.
        var clips = new[]
        {
            Clip("ok", wer: 0.10, cer: 0.05),
            new ClipResult("bad", 0.0, ExpectedSilent: false, HasReference: false,
                Reference: "", StreamText: "", BatchText: "", Wer: null, Cer: null, SilentPass: null,
                FinishMsRuns: Array.Empty<long>(), FellBack: false, FellBackCount: 0, Truncated: false,
                TrimmedSilent: false, BatchParityDiff: "", Error: "TranscribeCppException: secret failure details"),
        };

        var s = EvalResults.Summarize(clips);

        s.ClipCount.ShouldBe(2);
        s.ScoredCount.ShouldBe(1);
        s.FailedCount.ShouldBe(1);

        var md = EvalResults.ToMarkdown(Info, clips, s);
        md.ShouldContain("| bad |");
        md.ShouldContain("ERROR");
        md.ShouldContain("Failed: 1");
        md.ShouldNotContain("secret failure details"); // exception text stays out of results.md
    }

    [Fact]
    public void Summarize_batch_mode_pools_batch_times()
    {
        var clip = new ClipResult(
            "c1", 10.0, false, true, "ref text", "", "batch text",
            0.1, 0.05, null,
            FinishMsRuns: Array.Empty<long>(),
            FellBack: false, FellBackCount: 0, Truncated: false, TrimmedSilent: false,
            BatchParityDiff: "", Error: null,
            BatchMsRuns: new long[] { 800, 1200 },
            CpuSeconds: 3.5, MeanRtf: 0.4, TranscriptStable: true);
        var s = EvalResults.Summarize(new[] { clip }, mode: "batch", cpuSecondsTotal: 3.5, peakMemoryMb: 1234.5);
        s.LatencyP50Ms.ShouldBe(800);
        s.LatencyMaxMs.ShouldBe(1200);
        s.CpuSecondsTotal.ShouldBe(3.5);
        s.PeakMemoryMb.ShouldBe(1234.5);
        s.MeanRtf.ShouldBe(0.4);
    }

    [Fact]
    public void Summarize_counts_unstable_transcripts()
    {
        ClipResult Clip(string id, bool stable) => new(
            id, 5.0, false, true, "ref", "hyp", "hyp", 0.0, 0.0, null,
            new long[] { 100 }, false, 0, false, false, "", null,
            BatchMsRuns: new long[] { 100 }, CpuSeconds: 1, MeanRtf: 0.1, TranscriptStable: stable);
        var s = EvalResults.Summarize(new[] { Clip("a", true), Clip("b", false) });
        s.UnstableTranscriptCount.ShouldBe(1);
    }

    [Fact]
    public void ToJson_marks_mode_language_passes_converged_and_trace()
    {
        var info = new EvalRunInfo("corpus-v1", "qwen3-asr-1.7b", "0.1.3", "2026-07-27", 1,
            Mode: "batch", Language: null, Passes: 3, Converged: true);
        var clips = Array.Empty<ClipResult>();
        var summary = EvalResults.Summarize(clips, mode: "batch");
        var passes = new[] { new PassSummary(1, 500, 900, 1000, 12.3, 2048.0, 0.4, 0.15, 0) };
        var trace = new[] { new ConvergencePoint(1, 500, 20, 0.04, double.PositiveInfinity, false) };
        var json = EvalResults.ToJson(info, clips, summary, passes, trace);
        json.ShouldContain("\"mode\": \"batch\"");
        json.ShouldContain("\"passes\"");
        json.ShouldContain("\"converged\": true");
        json.ShouldContain("\"convergenceTrace\"");
        json.ShouldContain("\"ciHalfWidthMs\"");
        json.ShouldContain("\"deltaFromPrevious\"");
        json.ShouldContain("\"resourceNote\"");
        json.ShouldContain("\"cpuSeconds\"");
        json.ShouldContain("\"peakMemoryMb\"");
    }

    [Fact]
    public void ToJson_streaming_mode_marks_streaming_and_language()
    {
        var info = new EvalRunInfo("corpus-v1", "nemotron-3.5-asr-streaming-0.6b", "0.1.3", "2026-07-27", 1,
            Mode: "streaming", Language: "en-US");
        var json = EvalResults.ToJson(info, Array.Empty<ClipResult>(), EvalResults.Summarize(Array.Empty<ClipResult>()));
        json.ShouldContain("\"mode\": \"streaming\"");
        json.ShouldContain("\"language\": \"en-US\"");
    }

    [Fact]
    public void ToJson_serializes_infinite_ratio_without_throwing()
    {
        var trace = new[] { new ConvergencePoint(1, 0, 0, double.PositiveInfinity, double.PositiveInfinity, false) };
        var json = EvalResults.ToJson(
            new EvalRunInfo("c", "m", "0.1.3", "2026-07-27", 1),
            Array.Empty<ClipResult>(), EvalResults.Summarize(Array.Empty<ClipResult>()),
            Array.Empty<PassSummary>(), trace);
        json.ShouldContain("\"Infinity\"");
    }
}
