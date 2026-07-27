using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class EvalComparisonTests
{
    private static EvalReport MakeReport(string model, string mode, string? language, bool converged)
    {
        var info = new EvalRunInfo("corpus-v1", model, "0.1.3", "2026-07-27", 1,
            Mode: mode, Language: language, Passes: 2, Converged: converged);
        var clips = new[]
        {
            new ClipResult("c1", 10.0, false, true, "synthetic reference", "synthetic hyp", "synthetic hyp",
                0.25, 0.10, null, new long[] { 400 }, false, 0, false, false, "", null,
                BatchMsRuns: new long[] { 900 }, CpuSeconds: 2.0, MeanRtf: 0.2, TranscriptStable: true),
        };
        var summary = EvalResults.Summarize(clips, mode, cpuSecondsTotal: 2.0, peakMemoryMb: 1500.0);
        return new EvalReport(info, summary, clips,
            new[] { new PassSummary(1, 400, 400, 400, 1.0, 1400.0, 0.2, 0.25, 0),
                    new PassSummary(2, 400, 400, 400, 1.0, 1500.0, 0.2, 0.25, 0) },
            new[] { new ConvergencePoint(1, 400, 10, 0.025, double.PositiveInfinity, false),
                    new ConvergencePoint(2, 400, 10, 0.025, 0.0, true) });
    }

    [Fact]
    public void Roundtrips_results_json_through_Parse()
    {
        var report = MakeReport("nemotron-streaming-en", "streaming", null, true);
        var json = EvalResults.ToJson(report.Info, report.Clips, report.Summary,
            report.Passes!, report.ConvergenceTrace!);
        var parsed = EvalComparison.Parse(json);
        parsed.Info.SpeechModel.ShouldBe("nemotron-streaming-en");
        parsed.Info.Mode.ShouldBe("streaming");
        parsed.Clips.Count.ShouldBe(1);
        parsed.Passes!.Count.ShouldBe(2);
        parsed.ConvergenceTrace!.Count.ShouldBe(2);
    }

    [Fact]
    public void Build_aligns_models_and_carries_key_numbers()
    {
        var reports = new[]
        {
            MakeReport("qwen3-asr-1.7b", "batch", null, false),
            MakeReport("nemotron-3.5-asr-streaming-0.6b", "streaming", "en-US", true),
        };
        var c = EvalComparison.Build(reports, "2026-07-27");
        c.Corpus.ShouldBe("corpus-v1");
        c.Models.Count.ShouldBe(2);
        c.Models.Select(m => m.Model).ShouldBe(new[]
            { "nemotron-3.5-asr-streaming-0.6b", "qwen3-asr-1.7b" }); // sorted by model name
        var nem = c.Models[0];
        nem.Mode.ShouldBe("streaming");
        nem.Language.ShouldBe("en-US");
        nem.Converged.ShouldBeTrue();
        nem.MeanWer.ShouldBe(0.25);
        nem.LatencyP50Ms.ShouldBe(400);
        nem.CpuSecondsTotal.ShouldBe(2.0);
        nem.PeakMemoryMb.ShouldBe(1500.0);
        nem.ConvergenceTrace.Count.ShouldBe(2);
        var qwen = c.Models[1];
        qwen.Mode.ShouldBe("batch");
        qwen.LatencyP50Ms.ShouldBe(900); // batch mode pools batch times
    }

    [Fact]
    public void ToJson_contains_no_transcript_text()
    {
        var c = EvalComparison.Build(new[] { MakeReport("m", "streaming", null, false) }, "2026-07-27");
        var json = EvalComparison.ToJson(c);
        json.ShouldNotContain("synthetic reference");
        json.ShouldNotContain("synthetic hyp");
        json.ShouldContain("\"models\"");
        json.ShouldContain("\"converged\"");
    }
}
