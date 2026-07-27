using CleanupLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>Arg parsing for the cleanup latency bench's two scenarios
/// (linked BCL-only file scripts/cleanup-latency-bench/CleanupBenchArgs.cs).</summary>
public sealed class CleanupBenchArgsTests
{
    // ---- export-statements --------------------------------------------------

    [Fact]
    public void ParseExport_AllFlags_Parses()
    {
        var a = CleanupBenchArgs.ParseExport(
            new[] { "--history-dir", @"C:\hist", "--out", "statements.jsonl", "--max", "25" });

        a.Error.ShouldBeNull();
        a.HistoryDir.ShouldBe(@"C:\hist");
        a.OutFile.ShouldBe("statements.jsonl");
        a.Max.ShouldBe(25);
    }

    [Fact]
    public void ParseExport_MaxDefaultsToZero_MeaningAllEntries()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir", "h", "--out", "o" });

        a.Error.ShouldBeNull();
        a.Max.ShouldBe(0);
    }

    [Fact]
    public void ParseExport_MissingHistoryDir_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--out", "o" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--history-dir");
    }

    [Fact]
    public void ParseExport_MissingOut_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir", "h" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--out");
    }

    [Fact]
    public void ParseExport_NegativeMax_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir", "h", "--out", "o", "--max", "-1" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--max");
    }

    [Fact]
    public void ParseExport_NonNumericMax_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir", "h", "--out", "o", "--max", "many" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("many");
    }

    [Fact]
    public void ParseExport_UnknownFlag_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir", "h", "--out", "o", "--bogus" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--bogus");
    }

    [Fact]
    public void ParseExport_FlagWithoutValue_Errors()
    {
        var a = CleanupBenchArgs.ParseExport(new[] { "--history-dir" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--history-dir");
    }

    // ---- latency -------------------------------------------------------------

    [Fact]
    public void ParseLatency_Defaults_MatchDocumentedValues()
    {
        var a = CleanupBenchArgs.ParseLatency(new[] { "--statements", "s.jsonl", "--models-root", "m" });

        a.Error.ShouldBeNull();
        a.StatementsFile.ShouldBe("s.jsonl");
        a.ModelsRoot.ShouldBe("m");
        a.IncludeEvalCases.ShouldBeFalse();
        a.Model.ShouldBeNull();
        a.Passes.ShouldBe(3);
        a.OutDir.ShouldBe("artifacts/cleanup-bench-results");
        a.Seed.ShouldBe(42);
        a.TimeoutMs.ShouldBe(15_000);
    }

    [Fact]
    public void ParseLatency_AllFlags_Parses()
    {
        var a = CleanupBenchArgs.ParseLatency(new[]
        {
            "--statements", "s.jsonl", "--include-eval-cases", "--models-root", @"C:\models",
            "--model", "qwen2.5-0.5b-instruct-q4_k_m", "--passes", "5",
            "--out", @"C:\res", "--seed", "7", "--timeout-ms", "30000",
        });

        a.Error.ShouldBeNull();
        a.IncludeEvalCases.ShouldBeTrue();
        a.ModelsRoot.ShouldBe(@"C:\models");
        a.Model.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        a.Passes.ShouldBe(5);
        a.OutDir.ShouldBe(@"C:\res");
        a.Seed.ShouldBe(7);
        a.TimeoutMs.ShouldBe(30_000);
    }

    [Fact]
    public void ParseLatency_MissingStatements_Errors()
    {
        var a = CleanupBenchArgs.ParseLatency(new[] { "--models-root", "m" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--statements");
    }

    [Fact]
    public void ParseLatency_MissingModelsRoot_Errors()
    {
        var a = CleanupBenchArgs.ParseLatency(new[] { "--statements", "s" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--models-root");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void ParseLatency_NonPositivePasses_Errors(string passes)
    {
        var a = CleanupBenchArgs.ParseLatency(
            new[] { "--statements", "s", "--models-root", "m", "--passes", passes });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--passes");
    }

    [Fact]
    public void ParseLatency_NonNumericPasses_Errors()
    {
        var a = CleanupBenchArgs.ParseLatency(
            new[] { "--statements", "s", "--models-root", "m", "--passes", "three" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("three");
    }

    [Fact]
    public void ParseLatency_NegativeSeed_Errors()
    {
        // The seed is forwarded to LlamaCleanupBackend as a uint.
        var a = CleanupBenchArgs.ParseLatency(
            new[] { "--statements", "s", "--models-root", "m", "--seed", "-1" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--seed");
    }

    [Fact]
    public void ParseLatency_ZeroTimeout_Errors()
    {
        var a = CleanupBenchArgs.ParseLatency(
            new[] { "--statements", "s", "--models-root", "m", "--timeout-ms", "0" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--timeout-ms");
    }

    [Fact]
    public void ParseLatency_UnknownFlag_Errors()
    {
        var a = CleanupBenchArgs.ParseLatency(
            new[] { "--statements", "s", "--models-root", "m", "--fast" });

        a.Error.ShouldNotBeNull();
        a.Error.ShouldContain("--fast");
    }
}
