using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class InjectionChannelNamesTests
{
    [Theory]
    [InlineData("emReplaceSel", DeliveryChannel.EmReplaceSel)]
    [InlineData("EMREPLACESEL", DeliveryChannel.EmReplaceSel)]
    [InlineData("wmcharsmto", DeliveryChannel.WmCharSmto)]
    [InlineData("WmCharSmto", DeliveryChannel.WmCharSmto)]
    [InlineData("vkPacket", DeliveryChannel.VkPacket)]
    [InlineData("VKPACKET", DeliveryChannel.VkPacket)]
    public void TryParse_IsCaseInsensitive(string name, DeliveryChannel expected)
    {
        InjectionChannelNames.TryParse(name, out var channel).ShouldBeTrue();
        channel.ShouldBe(expected);
    }

    [Theory]
    [InlineData("clipboard")]
    [InlineData("wmCharFenced")] // falsified rung (E9b) must NOT parse
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsUnknownNames(string? name)
    {
        InjectionChannelNames.TryParse(name, out _).ShouldBeFalse();
    }

    [Fact]
    public void Name_RoundTrips_AllChannels()
    {
        InjectionChannelNames.Name(DeliveryChannel.EmReplaceSel).ShouldBe("emReplaceSel");
        InjectionChannelNames.Name(DeliveryChannel.WmCharSmto).ShouldBe("wmCharSmto");
        InjectionChannelNames.Name(DeliveryChannel.VkPacket).ShouldBe("vkPacket");
    }

    [Fact]
    public void DefaultLadder_IsPinnedOrder()
    {
        InjectionChannelNames.DefaultLadder.ShouldBe(new[]
        {
            DeliveryChannel.EmReplaceSel,
            DeliveryChannel.WmCharSmto,
            DeliveryChannel.VkPacket,
        });
    }

    [Fact]
    public void ParseLadder_HonorsConfiguredOrder()
    {
        var order = InjectionChannelNames.ParseLadder(new[] { "vkPacket", "emReplaceSel" });
        order.ShouldBe(new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });
    }

    [Fact]
    public void ParseLadder_UnknownNames_AreReportedAndSkipped()
    {
        var unknown = new List<string>();
        var order = InjectionChannelNames.ParseLadder(
            new[] { "clipboard", "wmCharSmto" }, unknown.Add);
        order.ShouldBe(new[] { DeliveryChannel.WmCharSmto });
        unknown.ShouldBe(new[] { "clipboard" });
    }

    [Fact]
    public void ParseLadder_NullEmptyOrAllInvalid_FallsBackToDefault()
    {
        InjectionChannelNames.ParseLadder(null).ShouldBe(InjectionChannelNames.DefaultLadder);
        InjectionChannelNames.ParseLadder(Array.Empty<string>()).ShouldBe(InjectionChannelNames.DefaultLadder);
        InjectionChannelNames.ParseLadder(new[] { "bogus" }).ShouldBe(InjectionChannelNames.DefaultLadder);
    }

    [Fact]
    public void ParseLadder_Duplicates_KeepFirstOccurrenceOnly()
    {
        var order = InjectionChannelNames.ParseLadder(new[] { "vkPacket", "vkPacket", "emReplaceSel" });
        order.ShouldBe(new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });
    }

    [Fact]
    public void DeliveryChannel_Default_IsVkPacket()
    {
        // Pinned: VkPacket == 0 so default(InjectionRunReport).Via is the
        // status-quo floor (design decision #1 in the plan).
        default(DeliveryChannel).ShouldBe(DeliveryChannel.VkPacket);
    }
}
