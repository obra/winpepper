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
}
