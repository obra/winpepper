using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class WmCharSmtoStrategyTests
{
    [Fact]
    public void Channel_IsWmCharSmto()
    {
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .Channel.ShouldBe(DeliveryChannel.WmCharSmto);
    }

    [Fact]
    public void Gate_Passes_WhenFocusedChildObservable_AndStable()
    {
        // Gate is exactly "focused child observable + stable": stability is
        // encoded upstream as a nonzero effective hwnd (pinned decision #2).
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .CanDeliver(42, 7).ShouldBeTrue();
    }

    [Fact]
    public void Gate_Fails_OnZeroFocusedChild()
    {
        new WmCharSmtoStrategy(NullLogger.Instance, (_, _) => true)
            .CanDeliver(42, 0).ShouldBeFalse();
    }

    [Fact]
    public void TrySendChunk_SendsOneMessagePerUtf16Unit_InOrder()
    {
        var sent = new List<(long Hwnd, ushort Unit)>();
        var strategy = new WmCharSmtoStrategy(
            NullLogger.Instance, (h, u) => { sent.Add((h, u)); return true; });

        // "a" + G-clef (U+1D11E, one surrogate pair = two units) + "b"
        var chunk = "a\uD834\uDD1Eb";
        strategy.TrySendChunk(7, chunk).ShouldBeTrue();

        sent.Select(s => s.Unit).ShouldBe(new ushort[] { 'a', 0xD834, 0xDD1E, 'b' });
        sent.All(s => s.Hwnd == 7).ShouldBeTrue();
    }

    [Fact]
    public void TrySendChunk_StopsAtFirstRefusedUnit_AndReturnsFalse()
    {
        var sentCount = 0;
        var strategy = new WmCharSmtoStrategy(
            NullLogger.Instance, (_, _) => ++sentCount < 3); // 3rd unit refused

        strategy.TrySendChunk(7, "abcdefgh").ShouldBeFalse();
        sentCount.ShouldBe(3); // units 4..8 never attempted
    }
}
