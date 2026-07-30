using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class DrainAbandonPolicyTests
{
    [Fact]
    public void NoCallInFlight_NeverAbandonsEarly()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            null, TimeSpan.FromSeconds(10)).ShouldBeFalse();

    [Fact]
    public void InFlightBelowBudget_WaitsOutTheDeadline()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(9.9), TimeSpan.FromSeconds(10)).ShouldBeFalse();

    [Fact]
    public void InFlightAtBudget_AbandonsImmediately()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)).ShouldBeTrue();

    [Fact]
    public void InFlightFarPastBudget_AbandonsImmediately()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(10)).ShouldBeTrue();

    // A15 pins: `elapsed >= budget` bounds the PAST, not the REMAINING time.
    // With the zero-push shortcut the effective deadline shrinks to ~1.5 s;
    // healthy 2.9-3.96 s calls (observed and RECOVERED in the field) must
    // never be abandoned there — abandon iff
    // elapsed >= max(effectiveDeadline, MinInFlightForFutility).
    [Fact]
    public void ShortEffectiveDeadline_HealthyClassCallInFlight_DoesNotAbandon()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(1.5)).ShouldBeFalse();

    [Fact]
    public void ShortEffectiveDeadline_RecoveredClassCall_DoesNotAbandon()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1.5)).ShouldBeFalse(); // native_max=3960 ms was a recovered dictation

    [Fact]
    public void ShortEffectiveDeadline_PastTheFutilityFloor_Abandons()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1.5)).ShouldBeTrue();
}
