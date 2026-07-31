using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class CpuPeggedPolicyTests
{
    [Theory]
    [InlineData(75, true)]   // at the threshold counts as pegged
    [InlineData(76, true)]
    [InlineData(100, true)]
    [InlineData(74, false)]
    [InlineData(0, false)]
    public void IsPegged_Compares_Against_The_Named_Threshold(int pct, bool expected)
        => CpuPeggedPolicy.IsPegged(pct).ShouldBe(expected);

    [Fact]
    public void No_Reading_Is_Not_Pegged()
        => CpuPeggedPolicy.IsPegged(null).ShouldBeFalse();

    [Fact]
    public void Threshold_Is_75_Percent()
        => CpuPeggedPolicy.SystemCpuPeggedThresholdPercent.ShouldBe(75);
}
