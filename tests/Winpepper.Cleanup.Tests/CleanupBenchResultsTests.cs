using CleanupLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>Result aggregation and rendering for the cleanup latency bench
/// (linked BCL-only file scripts/cleanup-latency-bench/CleanupBenchResults.cs).
/// The privacy split is the load-bearing contract: results.md carries numbers
/// and ids ONLY; results.json carries the full transcript text.</summary>
public sealed class CleanupBenchResultsTests
{
    private const string SecretMarker = "SECRET-TRANSCRIPT-MARKER";

    private static StatementResult Statement(
        string id, long[]? callMs = null, string[]? paths = null, string? error = null) =>
        new(id,
            CharCount: 42,
            WordCount: 9,
            InputText: $"{SecretMarker} raw input words",
            CallMsRuns: callMs ?? new long[] { 100 },
            ElapsedMsRuns: callMs ?? new long[] { 100 },
            PathRuns: paths ?? new[] { "Llm" },
            OutputRuns: new[] { $"{SecretMarker} cleaned output words" },
            Error: error);

    private static readonly BenchRunInfo Info = new(
        Model: "qwen2.5-0.5b-instruct-q4_k_m", PromptFormat: "chatml", DateUtc: "2026-07-27",
        Passes: 3, Seed: 42, TimeoutMs: 15_000, ModelLoadMs: 1234, WarmMs: 567,
        StatementsSource: "statements.jsonl + 18 eval cases");

    // ---- percentile math -------------------------------------------------------

    [Fact]
    public void Percentile_EmptyInput_ReturnsZero()
    {
        CleanupBenchResults.Percentile(Array.Empty<double>(), 0.5).ShouldBe(0);
    }

    [Fact]
    public void Percentile_NearestRankCeiling_MatchesAsrBenchSemantics()
    {
        var sorted = new double[] { 10, 20, 30, 40 };

        CleanupBenchResults.Percentile(sorted, 0.5).ShouldBe(20);   // ceil(0.5*4)-1 = idx 1
        CleanupBenchResults.Percentile(sorted, 0.95).ShouldBe(40);  // ceil(0.95*4)-1 = idx 3
        CleanupBenchResults.Percentile(sorted, 0.25).ShouldBe(10);
        CleanupBenchResults.Percentile(new double[] { 7 }, 0.95).ShouldBe(7);
    }

    // ---- summarize -------------------------------------------------------------

    [Fact]
    public void Summarize_LatencyStats_UseOnlyLlmPathCalls()
    {
        var statements = new[]
        {
            Statement("a", callMs: new long[] { 100, 200, 300 }, paths: new[] { "Llm", "Llm", "Llm" }),
            // Bypass calls are fast and must NOT dilute the LLM latency stats.
            Statement("b", callMs: new long[] { 1, 1, 1 },
                paths: new[] { "BypassShort", "BypassShort", "BypassShort" }),
            Statement("c", callMs: new long[] { 400 }, paths: new[] { "Llm" }),
        };

        var s = CleanupBenchResults.Summarize(statements);

        s.StatementCount.ShouldBe(3);
        s.LlmCallCount.ShouldBe(4);                    // 100 200 300 400
        s.LlmCallP50Ms.ShouldBe(200);
        s.LlmCallP95Ms.ShouldBe(400);
        s.LlmCallMeanMs.ShouldBe(250.0, tolerance: 1e-9);
        s.PathCounts["Llm"].ShouldBe(4);
        s.PathCounts["BypassShort"].ShouldBe(3);
        s.FailedCount.ShouldBe(0);
    }

    [Fact]
    public void Summarize_NoLlmCalls_YieldsZeroStatsNotCrash()
    {
        var s = CleanupBenchResults.Summarize(new[]
        {
            Statement("a", callMs: new long[] { 1 }, paths: new[] { "BypassShort" }),
        });

        s.LlmCallCount.ShouldBe(0);
        s.LlmCallP50Ms.ShouldBe(0);
        s.LlmCallP95Ms.ShouldBe(0);
        s.LlmCallMeanMs.ShouldBe(0);
    }

    [Fact]
    public void Summarize_CountsFailedStatements()
    {
        var s = CleanupBenchResults.Summarize(new[]
        {
            Statement("ok"),
            Statement("bad", error: $"LLamaException: {SecretMarker} boom"),
        });

        s.FailedCount.ShouldBe(1);
    }

    [Fact]
    public void Summarize_TimeoutFallback_IsAPathCountNotAFailure()
    {
        // A runner timeout is production behavior (FallbackTimeout path), not an error row.
        var s = CleanupBenchResults.Summarize(new[]
        {
            Statement("slow", callMs: new long[] { 15_001 }, paths: new[] { "FallbackTimeout" }),
        });

        s.FailedCount.ShouldBe(0);
        s.PathCounts["FallbackTimeout"].ShouldBe(1);
    }

    // ---- rendering: the privacy split -------------------------------------------

    [Fact]
    public void ToMarkdown_HasNumbersIdsAndSummary_ButNoTranscriptText()
    {
        var statements = new[]
        {
            Statement("3f2a1b4c5d6e7f8091a2b3c4d5e6f708", callMs: new long[] { 812, 790, 801 }),
            Statement("bad", error: $"LLamaException: {SecretMarker} boom"),
        };
        var summary = CleanupBenchResults.Summarize(statements);

        var md = CleanupBenchResults.ToMarkdown(Info, statements, summary);

        md.ShouldContain("qwen2.5-0.5b-instruct-q4_k_m");
        md.ShouldContain("prompt format: `chatml`");
        md.ShouldContain("| 3f2a1b4c5d6e7f8091a2b3c4d5e6f708 | 42 | 9 | 812 790 801 | Llm | - |");
        md.ShouldContain("seed: 42");
        md.ShouldContain("timeout: 15000 ms");
        md.ShouldContain("passes: 3");
        md.ShouldContain("model load: 1234 ms, warm: 567 ms");
        md.ShouldContain("**Summary:**");
        md.ShouldContain("Failed: 1");
        md.ShouldContain("ERROR");
        // Input text, output text and exception text never leak into results.md.
        md.ShouldNotContain(SecretMarker);
    }

    [Fact]
    public void ToJson_CarriesFullInputOutputAndErrorText()
    {
        var statements = new[]
        {
            Statement("clip1"),
            Statement("bad", error: $"LLamaException: {SecretMarker} boom"),
        };
        var summary = CleanupBenchResults.Summarize(statements);

        var json = CleanupBenchResults.ToJson(Info, statements, summary);

        json.ShouldContain(SecretMarker);                            // full transcripts present
        json.ShouldContain("\"inputText\"");
        json.ShouldContain("\"outputRuns\"");
        json.ShouldContain("\"callMsRuns\"");
        json.ShouldContain($"\"model\": \"qwen2.5-0.5b-instruct-q4_k_m\"");
        json.ShouldContain("\"promptFormat\": \"chatml\"");
        json.ShouldContain("boom");                                  // error detail json-only
    }

    [Fact]
    public void WordCount_SplitsOnWhitespace()
    {
        CleanupBenchResults.WordCount("um hello  world\tnow").ShouldBe(4);
        CleanupBenchResults.WordCount("").ShouldBe(0);
        CleanupBenchResults.WordCount("   ").ShouldBe(0);
    }
}
