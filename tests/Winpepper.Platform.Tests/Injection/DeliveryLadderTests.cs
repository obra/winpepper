using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class DeliveryLadderTests
{
    private sealed class FakeStrategy : IDeliveryStrategy
    {
        private readonly bool _canDeliver;
        public int CanDeliverCalls;
        public readonly List<long> GatedHwnds = new();

        public FakeStrategy(DeliveryChannel channel, bool canDeliver)
        {
            Channel = channel;
            _canDeliver = canDeliver;
        }

        public DeliveryChannel Channel { get; }

        public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        {
            CanDeliverCalls++;
            GatedHwnds.Add(focusedChildHwnd);
            return _canDeliver;
        }

        public bool TrySendChunk(long targetHwnd, string chunk) => true;
    }

    private static readonly IReadOnlyList<DeliveryChannel> FullOrder =
        InjectionChannelNames.DefaultLadder;

    [Fact]
    public void FirstPassingGate_Wins_AndLaterGatesAreNotEvaluated()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(em);
        selection.GatesSummary.ShouldBe(string.Empty); // empty when the first rung delivered
        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(0);
        vk.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void GatesAreEvaluated_InConfiguredOrder_OnceEach()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { vk, wm, em }, // registration order irrelevant
            42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk);
        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(1);
        vk.CanDeliverCalls.ShouldBe(1);
    }

    [Fact]
    public void ConfiguredOrder_IsHonored()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel },
            new IDeliveryStrategy[] { em, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk); // vkPacket listed first wins
        em.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void GatesSummary_RecordsGatedRungs_WithStableFocusReason()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(wm);
        selection.GatesSummary.ShouldBe("emReplaceSel:no-em");
    }

    [Fact]
    public void GatesSummary_UsesFocusUnstableReason_WhenCaptureUnstable()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new FakeStrategy(DeliveryChannel.WmCharSmto, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            FullOrder, new IDeliveryStrategy[] { em, wm, vk },
            42, new FocusedChildCapture(0, false));

        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe("emReplaceSel:focus-unstable,wmCharSmto:focus-unstable");
    }

    [Fact]
    public void Gates_ReceiveTheEffectiveFocusedChildHwnd()
    {
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        DeliveryLadder.Select(FullOrder, new IDeliveryStrategy[] { em }, 42,
            new FocusedChildCapture(7, true));
        em.GatedHwnds.ShouldBe(new[] { 7L });
    }

    [Fact]
    public void ExhaustedLadder_FallsBackToVkPacketFloor_AndKeepsGatesRecord()
    {
        // Settings removed vkPacket and everything else gated out: degrade
        // to the status-quo floor rather than dropping the run (pinned
        // decision #4; design doc §3 "rungs degrade to the VK_PACKET floor").
        var em = new FakeStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);

        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.EmReplaceSel },
            new IDeliveryStrategy[] { em, vk }, 42, new FocusedChildCapture(7, true));

        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe("emReplaceSel:no-em");
    }

    [Fact]
    public void OrderNamingAnUnregisteredChannel_IsSkipped()
    {
        var vk = new FakeStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var selection = DeliveryLadder.Select(
            new[] { DeliveryChannel.EmReplaceSel, DeliveryChannel.VkPacket },
            new IDeliveryStrategy[] { vk }, 42, new FocusedChildCapture(7, true));
        selection.Strategy.ShouldBeSameAs(vk);
        selection.GatesSummary.ShouldBe(string.Empty); // absent rung is not a gate-out
    }
}
