using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class DeadlinePacerTests
{
    [Theory]
    [InlineData(0.0, 14)]  // free send => full pause (old behavior is the degenerate case)
    [InlineData(5.0, 9)]
    [InlineData(5.5, 9)]   // ceil(8.5) = 9: round UP, never undershoot the period
    [InlineData(13.2, 1)]
    public void PauseForNextChunk_SleepsTheCeilingRemainder(double elapsedMs, int expectedSleep)
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        sleeps.ShouldBe(new[] { expectedSleep });
    }

    [Theory]
    [InlineData(14.0)]
    [InlineData(20.0)]
    public void PauseForNextChunk_WorkAtOrPastThePeriod_DoesNotSleep(double elapsedMs)
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        // The feed is then throttled by SendInput itself, which is
        // inherently at or below the safe rate.
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void PeriodAccounting_RestartsAtTheEndOfEachPause()
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, ms => { sleeps.Add(ms); now += ms; }, () => now);

        now += 5.0;                // chunk 1 "send" costs 5 ms
        pacer.PauseForNextChunk(); // sleeps 9 -> clock 14, period restarts
        now += 5.0;                // chunk 2 "send" costs 5 ms
        pacer.PauseForNextChunk(); // must again be 9 (not 14 - 19)

        sleeps.ShouldBe(new[] { 9, 9 });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(5.5)]
    [InlineData(13.9)]
    [InlineData(14.0)]
    [InlineData(50.0)]
    public void BleedCeiling_Invariant_WorkPlusSleepNeverUndershootsThePeriod(double elapsedMs)
    {
        // THE invariant CHANGE 1 must provably keep (standard 8-unit
        // chunks; the 9-unit straddle sibling test below covers the scaled
        // period): per-chunk period
        // (send + sleep) >= InterChunkPauseMs, so nominal feed
        // <= ChunkCodeUnits/InterChunkPauseMs = ~571 units/s
        // <= TargetFeedUnitsPerSecond (600). Ceiling rounding is what makes
        // this hold for fractional elapsed values.
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(TextInjector.InterChunkPauseMs, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        (elapsedMs + sleeps.Sum()).ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs);
    }

    [Fact]
    public void PauseForNextChunk_PerCallPeriod_OverridesTheDefault()
    {
        // A 9-unit surrogate-straddle chunk gets a scaled 16 ms period
        // (stage-2 ledger A7); the pacer must honor the per-call value.
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = 5.0;
        pacer.PauseForNextChunk(16);

        sleeps.ShouldBe(new[] { 11 }); // 16 - 5
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(14.9)]
    [InlineData(15.0)]
    [InlineData(15.9)]
    public void BleedCeiling_Invariant_HoldsForNineUnitStraddleChunkPeriods(double elapsedMs)
    {
        // A 9-unit chunk needs a period of at least 9 * 1000 / 600 = 15 ms
        // to keep feed <= 600 units/s; the scaled period
        // ceil(9 * InterChunkPauseMs / ChunkCodeUnits) = 16 ms provides it
        // with 1 ms margin (stage-2 ledger A7).
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(TextInjector.InterChunkPauseMs, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk(16);

        (elapsedMs + sleeps.Sum()).ShouldBeGreaterThanOrEqualTo(15.0);
    }

    [Fact]
    public void Ctor_RejectsNonPositivePeriod()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new DeadlinePacer(0, _ => { }, () => 0.0));
    }
}
