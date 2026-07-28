using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class HwndZeroMeterTests
{
    [Fact]
    public void Fresh_Meter_HasZeroCounts()
    {
        var meter = new HwndZeroMeter();
        meter.AtStartCount.ShouldBe(0);
        meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void RecordAtStart_IncrementsOnlyAtStart_AndReturnsRunningCount()
    {
        var meter = new HwndZeroMeter();
        meter.RecordAtStart().ShouldBe(1);
        meter.RecordAtStart().ShouldBe(2);
        meter.AtStartCount.ShouldBe(2);
        meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void RecordMidStream_IncrementsOnlyMidStream_AndReturnsRunningCount()
    {
        var meter = new HwndZeroMeter();
        meter.RecordMidStream().ShouldBe(1);
        meter.MidStreamCount.ShouldBe(1);
        meter.AtStartCount.ShouldBe(0);
    }
}
