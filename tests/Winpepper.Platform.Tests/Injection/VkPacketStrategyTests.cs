using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class VkPacketStrategyTests
{
    [Fact]
    public void Channel_IsVkPacket()
    {
        new VkPacketStrategy(_ => true).Channel.ShouldBe(DeliveryChannel.VkPacket);
    }

    [Fact]
    public void Gate_AlwaysPasses_EvenWithZeroFocusedChild()
    {
        var strategy = new VkPacketStrategy(_ => true);
        strategy.CanDeliver(42, 0).ShouldBeTrue();
        strategy.CanDeliver(0, 0).ShouldBeTrue();
    }

    [Fact]
    public void TrySendChunk_DelegatesToWrappedSend_IgnoringTargetHwnd()
    {
        var sent = new List<string>();
        var strategy = new VkPacketStrategy(c => { sent.Add(c); return true; });

        strategy.TrySendChunk(0, "hello wo").ShouldBeTrue();   // hwnd irrelevant
        strategy.TrySendChunk(999, "rld").ShouldBeTrue();
        sent.ShouldBe(new[] { "hello wo", "rld" });
    }

    [Fact]
    public void TrySendChunk_PropagatesFailure()
    {
        new VkPacketStrategy(_ => false).TrySendChunk(7, "x").ShouldBeFalse();
    }
}
