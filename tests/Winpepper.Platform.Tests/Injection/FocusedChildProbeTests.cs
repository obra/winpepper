using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class FocusedChildProbeTests
{
    [Fact]
    public void EqualNonzeroSamples_AreStable_WithThirtyMsGap()
    {
        var sleeps = new List<int>();
        var capture = FocusedChildProbe.Capture(42, _ => 7, sleeps.Add);

        capture.Stable.ShouldBeTrue();
        capture.FocusedChildHwnd.ShouldBe(7L);
        sleeps.ShouldBe(new[] { 30 }); // >= 30 ms between the two samples
    }

    [Fact]
    public void DisagreeingSamples_AreUnstable_WithZeroEffectiveHwnd()
    {
        var sample = 0;
        var capture = FocusedChildProbe.Capture(42, _ => ++sample == 1 ? 7L : 9L, _ => { });

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L); // unstable => effective hwnd is 0
    }

    [Fact]
    public void SecondSampleZero_IsUnstable()
    {
        var sample = 0;
        var capture = FocusedChildProbe.Capture(42, _ => ++sample == 1 ? 7L : 0L, _ => { });

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L);
    }

    [Fact]
    public void FirstSampleZero_IsUnstable_WithoutSleepingOrResampling()
    {
        // Pinned decision #9: a zero first sample already determines the
        // outcome; skip the 30 ms gap (keeps fake-hwnd unit tests and the
        // production no-focus path free of a pointless stall).
        var calls = 0;
        var sleeps = new List<int>();
        var capture = FocusedChildProbe.Capture(42, _ => { calls++; return 0; }, sleeps.Add);

        capture.Stable.ShouldBeFalse();
        capture.FocusedChildHwnd.ShouldBe(0L);
        calls.ShouldBe(1);
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void SamplerReceives_TheForegroundHwnd()
    {
        var seen = new List<long>();
        FocusedChildProbe.Capture(42, h => { seen.Add(h); return 7; }, _ => { });
        seen.ShouldBe(new[] { 42L, 42L });
    }
}
