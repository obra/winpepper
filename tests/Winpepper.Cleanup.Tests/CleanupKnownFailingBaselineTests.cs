using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Pure tests for the known-failing baseline lookup (runs on every platform,
/// no model). The baseline itself is model-gated debt tracked for kata 809b;
/// these tests guard the lookup semantics and the structural integrity of the
/// entries (no typo'd case names, no stale model names, no empty reasons).
/// </summary>
public class CleanupKnownFailingBaselineTests
{
    private const string BaselinedModel = "qwen2.5-0.5b-instruct-q4_k_m";

    [Fact]
    public void TryGetBaselineReason_KnownPair_ReturnsTrueWithReason()
    {
        CleanupEvalCases.TryGetBaselineReason(BaselinedModel, "trap-joke-request", out var reason)
            .ShouldBeTrue();
        reason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryGetBaselineReason_UnknownCase_ReturnsFalse()
    {
        CleanupEvalCases.TryGetBaselineReason(BaselinedModel, "guard-long-multisentence", out var reason)
            .ShouldBeFalse();
        reason.ShouldBeEmpty();
    }

    [Fact]
    public void TryGetBaselineReason_SameCaseDifferentModel_ReturnsFalse()
    {
        // Baseline entries are pinned to the exact model that produced the
        // observed output; a different (e.g. upgraded) model must run clean.
        CleanupEvalCases.TryGetBaselineReason("some-other-model", "trap-joke-request", out _)
            .ShouldBeFalse();
    }

    [Fact]
    public void Baseline_CaseNames_AllExistInCaseSet()
    {
        // A renamed or deleted eval case must not leave a dangling baseline
        // entry that would silently stop matching.
        var known = CleanupEvalCases.All.Select(c => c.Name).ToHashSet();
        foreach (var (key, _) in CleanupEvalCases.KnownFailingBaseline)
        {
            known.ShouldContain(key.CaseName,
                $"baseline entry references unknown eval case '{key.CaseName}'");
        }
    }

    [Fact]
    public void Baseline_ModelNames_AllExistInRegistry()
    {
        // A baseline pinned to a model no longer in the registry is dead debt.
        var registryNames = CleanupEvalModels.CleanupModels.Select(d => d.Name).ToHashSet();
        foreach (var (key, _) in CleanupEvalCases.KnownFailingBaseline)
        {
            registryNames.ShouldContain(key.Model,
                $"baseline entry references model '{key.Model}' absent from the registry");
        }
    }

    [Fact]
    public void Baseline_Reasons_RecordObservationDate()
    {
        // Policy: never grow the baseline without recording the observed
        // output and date. A yyyy-MM-dd date in the reason is the cheap
        // mechanical proxy for that.
        foreach (var (key, reason) in CleanupEvalCases.KnownFailingBaseline)
        {
            reason.ShouldMatch(@"\d{4}-\d{2}-\d{2}",
                $"baseline entry ({key.Model}, {key.CaseName}) must record the observation date");
        }
    }
}
