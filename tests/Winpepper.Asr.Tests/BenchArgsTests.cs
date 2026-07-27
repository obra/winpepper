using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class BenchArgsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ValidateRepeats_AtLeastOne_IsValid(int repeats)
        => BenchArgs.ValidateRepeats(repeats).ShouldBeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ValidateRepeats_BelowOne_YieldsClearError(int repeats)
    {
        var error = BenchArgs.ValidateRepeats(repeats);

        error.ShouldNotBeNull();
        error.ShouldContain("--repeats must be >= 1");
        error.ShouldContain(repeats.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void ValidateMaxClips_accepts_zero_or_positive(int v)
        => BenchArgs.ValidateMaxClips(v).ShouldBeNull();

    [Fact]
    public void ValidateMaxClips_rejects_negative()
        => BenchArgs.ValidateMaxClips(-1).ShouldNotBeNull();

    [Theory]
    [InlineData(0.0)]
    [InlineData(55.0)]
    public void ValidateTimeBudgetMinutes_accepts_zero_or_positive(double v)
        => BenchArgs.ValidateTimeBudgetMinutes(v).ShouldBeNull();

    [Fact]
    public void ValidateTimeBudgetMinutes_rejects_negative()
        => BenchArgs.ValidateTimeBudgetMinutes(-0.1).ShouldNotBeNull();

    [Theory]
    [InlineData(1, 0)]   // unlimited max
    [InlineData(2, 2)]
    [InlineData(2, 10)]
    public void ValidatePasses_accepts_valid_combinations(int min, int max)
        => BenchArgs.ValidatePasses(min, max).ShouldBeNull();

    [Theory]
    [InlineData(0, 0)]   // min must be >= 1
    [InlineData(3, 2)]   // max below min
    [InlineData(1, -1)]  // negative max
    public void ValidatePasses_rejects_invalid_combinations(int min, int max)
        => BenchArgs.ValidatePasses(min, max).ShouldNotBeNull();

    [Theory]
    [InlineData(1, 0.0)]    // bounded passes, no time budget
    [InlineData(0, 55.0)]   // unlimited passes, bounded time budget
    [InlineData(3, 10.0)]   // both bounded
    public void ValidateStopCondition_accepts_at_least_one_bound(int maxPasses, double budget)
        => BenchArgs.ValidateStopCondition(maxPasses, budget).ShouldBeNull();

    [Fact]
    public void ValidateStopCondition_rejects_unlimited_passes_with_no_budget()
        => BenchArgs.ValidateStopCondition(0, 0.0).ShouldNotBeNull();

    // ---- ResolveRepeats: the --repeats back-compat rule ----

    [Fact]
    public void ResolveRepeats_without_repeats_passes_convergence_values_through()
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: false, repeats: 1,
            timeBudgetSet: false, timeBudgetMinutes: 55.0,
            minPassesSet: false, minPasses: 2,
            maxPassesSet: false, maxPasses: 0);

        r.Error.ShouldBeNull();
        r.LegacyMode.ShouldBeFalse();
        r.TimeBudgetMinutes.ShouldBe(55.0);
        r.MinPasses.ShouldBe(2);
        r.MaxPasses.ShouldBe(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ResolveRepeats_alone_restores_legacy_exactly_n_runs_single_pass_semantics(int n)
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: true, repeats: n,
            timeBudgetSet: false, timeBudgetMinutes: 55.0,
            minPassesSet: false, minPasses: 2,
            maxPassesSet: false, maxPasses: 0);

        r.Error.ShouldBeNull();
        r.LegacyMode.ShouldBeTrue();
        // min == max == N pins the run to exactly N passes (each clip runs N times);
        // budget 0 removes the time cutoff so the run can never be shortened.
        r.MinPasses.ShouldBe(n);
        r.MaxPasses.ShouldBe(n);
        r.TimeBudgetMinutes.ShouldBe(0.0);
    }

    [Fact]
    public void ResolveRepeats_with_time_budget_keeps_new_meaning_as_pass_cap()
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: true, repeats: 4,
            timeBudgetSet: true, timeBudgetMinutes: 10.0,
            minPassesSet: false, minPasses: 2,
            maxPassesSet: false, maxPasses: 0);

        r.Error.ShouldBeNull();
        r.LegacyMode.ShouldBeFalse();
        r.MaxPasses.ShouldBe(4);          // --repeats = pass cap
        r.MinPasses.ShouldBe(2);          // convergence defaults untouched
        r.TimeBudgetMinutes.ShouldBe(10.0);
    }

    [Fact]
    public void ResolveRepeats_with_min_passes_keeps_new_meaning_as_pass_cap()
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: true, repeats: 5,
            timeBudgetSet: false, timeBudgetMinutes: 55.0,
            minPassesSet: true, minPasses: 3,
            maxPassesSet: false, maxPasses: 0);

        r.Error.ShouldBeNull();
        r.LegacyMode.ShouldBeFalse();
        r.MaxPasses.ShouldBe(5);
        r.MinPasses.ShouldBe(3);
        r.TimeBudgetMinutes.ShouldBe(55.0);
    }

    [Fact]
    public void ResolveRepeats_with_explicit_max_passes_is_rejected_as_ambiguous()
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: true, repeats: 3,
            timeBudgetSet: false, timeBudgetMinutes: 55.0,
            minPassesSet: false, minPasses: 2,
            maxPassesSet: true, maxPasses: 6);

        r.Error.ShouldNotBeNull();
        r.Error.ShouldContain("--repeats");
        r.Error.ShouldContain("--max-passes");
        // Untouched inputs when rejected.
        r.MaxPasses.ShouldBe(6);
        r.MinPasses.ShouldBe(2);
        r.TimeBudgetMinutes.ShouldBe(55.0);
    }

    [Fact]
    public void ResolveRepeats_legacy_result_satisfies_the_stop_condition()
    {
        var r = BenchArgs.ResolveRepeats(
            repeatsSet: true, repeats: 2,
            timeBudgetSet: false, timeBudgetMinutes: 55.0,
            minPassesSet: false, minPasses: 2,
            maxPassesSet: false, maxPasses: 0);

        BenchArgs.ValidateStopCondition(r.MaxPasses, r.TimeBudgetMinutes).ShouldBeNull();
        BenchArgs.ValidatePasses(r.MinPasses, r.MaxPasses).ShouldBeNull();
    }
}
