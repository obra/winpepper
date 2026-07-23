using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class VoiceMeterTests
{
    [Theory]
    // silence -> no bars
    [InlineData(0.0, 5, 0)]
    [InlineData(-0.5, 5, 0)]   // negative clamps to 0
    // any audible level lights at least one bar
    [InlineData(0.01, 5, 1)]
    [InlineData(0.20, 5, 1)]   // ceil(0.20*5)=1
    [InlineData(0.21, 5, 2)]   // ceil(1.05)=2
    [InlineData(0.50, 5, 3)]   // ceil(2.5)=3
    [InlineData(0.80, 5, 4)]   // ceil(4.0)=4
    [InlineData(1.00, 5, 5)]   // full scale
    [InlineData(1.50, 5, 5)]   // above range clamps to barCount
    public void BarsLit_MapsLevelToBarCount(double level, int barCount, int expected)
        => VoiceMeter.BarsLit(level, barCount).ShouldBe(expected);

    [Fact]
    public void BarsLit_NeverExceedsBarCount()
        => VoiceMeter.BarsLit(0.99, 3).ShouldBe(3); // ceil(2.97)=3, capped at 3

    [Fact]
    public void BarsLit_RejectsNonPositiveBarCount()
        => Should.Throw<System.ArgumentOutOfRangeException>(() => VoiceMeter.BarsLit(0.5, 0));

    [Theory]
    // Perceptual dB mapping: -50 dBFS floor .. -10 dBFS ceiling -> 0..1
    [InlineData(0.0, 0.0)]        // silence
    [InlineData(-1.0, 0.0)]       // negative clamps to 0
    [InlineData(0.001, 0.0)]      // -60 dB, below floor
    [InlineData(0.00316, 0.0)]    // -50 dB, at floor
    [InlineData(0.0316, 0.5)]     // -30 dB, midpoint
    [InlineData(0.316, 1.0)]      // -10 dB, at ceiling
    [InlineData(1.0, 1.0)]        // 0 dBFS clamps to 1
    [InlineData(2.0, 1.0)]        // above range clamps to 1
    public void Perceptual_MapsDbRangeToUnitInterval(double linear, double expected)
        => VoiceMeter.Perceptual(linear).ShouldBe(expected, tolerance: 0.02);

    [Fact]
    public void Perceptual_TypicalSpeechPeak_LightsMultipleBars()
    {
        // Regression: raw speech peaks (~0.05..0.3 linear) previously mapped to
        // a single stuck bar. Perceptually they must light 2+ of 5 bars.
        VoiceMeter.BarsLit(VoiceMeter.Perceptual(0.05), 5).ShouldBeGreaterThanOrEqualTo(2);
        VoiceMeter.BarsLit(VoiceMeter.Perceptual(0.15), 5).ShouldBeGreaterThanOrEqualTo(3);
        VoiceMeter.BarsLit(VoiceMeter.Perceptual(0.3), 5).ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public void BarHeights_SilenceYieldsAllZero()
    {
        var h = VoiceMeter.BarHeights(0.0, tick: 7, barCount: 12);
        h.Length.ShouldBe(12);
        h.ShouldAllBe(v => v == 0.0);
    }

    [Fact]
    public void BarHeights_CenterWeighted_MiddleBarsTallestOnAverage()
    {
        // Average over many ticks so per-bar shimmer cancels out.
        var sums = new double[12];
        for (var t = 0; t < 200; t++)
        {
            var h = VoiceMeter.BarHeights(0.8, t, 12);
            for (var i = 0; i < 12; i++) sums[i] += h[i];
        }
        var mid = (sums[5] + sums[6]) / 2;
        mid.ShouldBeGreaterThan(sums[0]);
        mid.ShouldBeGreaterThan(sums[11]);
    }

    [Fact]
    public void BarHeights_AllValuesClamped01()
    {
        for (var t = 0; t < 50; t++)
            foreach (var v in VoiceMeter.BarHeights(1.5, t, 12)) // over-range level
            {
                v.ShouldBeGreaterThanOrEqualTo(0.0);
                v.ShouldBeLessThanOrEqualTo(1.0);
            }
    }

    [Fact]
    public void BarHeights_DeterministicForSameInputs()
        => VoiceMeter.BarHeights(0.5, 42, 12).ShouldBe(VoiceMeter.BarHeights(0.5, 42, 12));

    [Fact]
    public void BarHeights_VariesAcrossTicks()
        => VoiceMeter.BarHeights(0.5, 1, 12).ShouldNotBe(VoiceMeter.BarHeights(0.5, 5, 12));

    [Fact]
    public void BarHeights_RejectsNonPositiveBarCount()
        => Should.Throw<System.ArgumentOutOfRangeException>(() => VoiceMeter.BarHeights(0.5, 0, 0));
}
