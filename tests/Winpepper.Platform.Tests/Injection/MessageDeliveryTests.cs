using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class MessageDeliveryTests
{
    // Off-Windows every wrapper must fail closed (null/false/0) so the
    // ladder degrades to VkPacket instead of throwing — the same
    // OperatingSystem.IsWindows() guard discipline as ElevationProbe.
    [Fact]
    public void OffWindows_AllWrappers_FailClosed()
    {
        if (OperatingSystem.IsWindows()) return; // Linux-only pin

        MessageDelivery.ClassName(42).ShouldBeNull();
        MessageDelivery.EmGetSelProbe(42).ShouldBeFalse();
        MessageDelivery.SendReplaceSel(42, "hi").ShouldBeFalse();
        MessageDelivery.SendCharSmto(42, 'h').ShouldBeFalse();
        MessageDelivery.SampleFocusedChild(42).ShouldBe(0L);
    }

    [Fact]
    public void ZeroHwnd_FailsClosed_OnAnyPlatform()
    {
        MessageDelivery.ClassName(0).ShouldBeNull();
        MessageDelivery.EmGetSelProbe(0).ShouldBeFalse();
        MessageDelivery.SendReplaceSel(0, "hi").ShouldBeFalse();
        MessageDelivery.SendCharSmto(0, 'h').ShouldBeFalse();
        MessageDelivery.SampleFocusedChild(0).ShouldBe(0L);
    }
}
