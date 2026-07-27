using CleanupLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Guards the SINGLE SOURCE OF TRUTH contract between the committed eval case
/// texts (scripts/cleanup-latency-bench/CleanupEvalStatements.cs, shared with
/// the cleanup latency bench) and the behavioral eval cases built on them
/// (CleanupEvalCases). Runs on every platform with no model.
/// </summary>
public sealed class CleanupEvalStatementsTests
{
    [Fact]
    public void Has18Statements_WithUniqueNames()
    {
        CleanupEvalStatements.All.Count.ShouldBe(18);
        CleanupEvalStatements.All.Select(s => s.Name).ShouldBeUnique();
    }

    [Fact]
    public void EveryStatement_HasNonEmptyRawTranscript()
    {
        foreach (var s in CleanupEvalStatements.All)
        {
            s.RawTranscript.ShouldNotBeNullOrWhiteSpace($"statement '{s.Name}' has an empty raw transcript");
        }
    }

    [Fact]
    public void StatementsAndEvalCases_AreInLockstep()
    {
        // Same names, same order, same raw texts: the bench's --include-eval-cases
        // replays EXACTLY what the model-gated prompt eval suite asserts on.
        CleanupEvalCases.All.Count.ShouldBe(CleanupEvalStatements.All.Count);
        CleanupEvalCases.All.Select(c => c.Name)
            .ShouldBe(CleanupEvalStatements.All.Select(s => s.Name));
        foreach (var evalCase in CleanupEvalCases.All)
        {
            evalCase.RawTranscript.ShouldBe(CleanupEvalStatements.RawFor(evalCase.Name),
                $"case '{evalCase.Name}' drifted from its shared statement text");
        }
    }

    [Fact]
    public void SpotCheck_KnownStatementsAreIntact()
    {
        // Anchors against accidental edits during refactors (the behavioral
        // baselines in CleanupEvalCases.KnownFailingBaseline reference these
        // exact dictations).
        CleanupEvalStatements.RawFor("trap-synonym-question")
            .ShouldBe("What is a synonym for whisper?");
        CleanupEvalStatements.RawFor("corr-recipient-scratch")
            .ShouldBe("Send the report to Becca scratch that send it to Pete before the meeting.");
        CleanupEvalStatements.RawFor("guard-long-multisentence")
            .ShouldStartWith("Okay so um first I want to thank everyone");
    }

    [Fact]
    public void RawFor_UnknownName_ThrowsLoudly()
    {
        Should.Throw<ArgumentException>(() => CleanupEvalStatements.RawFor("no-such-case"));
    }
}
