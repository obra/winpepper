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
}
