using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorLadderTests
{
    private sealed class RecordingStrategy : IDeliveryStrategy
    {
        private readonly bool _canDeliver;
        private readonly Func<string, bool> _send;
        public int CanDeliverCalls;
        public readonly List<(long Hwnd, string Chunk)> Sent = new();
        public int SendCallsAtFirstGate = -1;

        public RecordingStrategy(DeliveryChannel channel, bool canDeliver, Func<string, bool>? send = null)
        {
            Channel = channel;
            _canDeliver = canDeliver;
            _send = send ?? (_ => true);
        }

        public DeliveryChannel Channel { get; }

        public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        {
            if (SendCallsAtFirstGate < 0) SendCallsAtFirstGate = Sent.Count;
            CanDeliverCalls++;
            return _canDeliver;
        }

        public bool TrySendChunk(long targetHwnd, string chunk)
        {
            Sent.Add((targetHwnd, chunk));
            return _send(chunk);
        }
    }

    private static TextInjector NewInjector(
        IReadOnlyList<IDeliveryStrategy> strategies,
        FocusedChildCapture capture,
        IReadOnlyList<DeliveryChannel>? order = null,
        Func<string, bool>? sendChunk = null)
        => new(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: sendChunk,
            sleep: _ => { },
            foregroundElevation: null,
            monotonicMs: null,
            channelOrder: () => order ?? InjectionChannelNames.DefaultLadder,
            focusedChildCapture: _ => capture,
            strategies: strategies);

    [Fact]
    public void WholeRun_DeliveredByFirstPassingRung_ToTheFixedTarget()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var wm = new RecordingStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, wm, vk },
            new FocusedChildCapture(7, true));
        var text = new string('a', 80); // 10 chunks of 8

        var report = injector.TryInjectGuardedDetailed(text);

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.EmReplaceSel);
        report.GatesSummary.ShouldBeNullOrEmpty();
        em.Sent.Count.ShouldBe(10);
        string.Concat(em.Sent.Select(s => s.Chunk)).ShouldBe(text);
        em.Sent.All(s => s.Hwnd == 7).ShouldBeTrue(); // SAME hwnd for every chunk
        wm.Sent.ShouldBeEmpty();
        vk.Sent.ShouldBeEmpty();
    }

    [Fact]
    public void Gates_RunOnce_BeforeAnyTextIsSent()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: false);
        var wm = new RecordingStrategy(DeliveryChannel.WmCharSmto, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, wm },
            new FocusedChildCapture(7, true));

        injector.TryInjectGuardedDetailed(new string('a', 80))
            .Outcome.ShouldBe(InjectionRunOutcome.Completed);

        em.CanDeliverCalls.ShouldBe(1);
        wm.CanDeliverCalls.ShouldBe(1);
        wm.SendCallsAtFirstGate.ShouldBe(0); // gate evaluated before any chunk went out
    }

    [Fact]
    public void MidRunSendFailure_StopsTheRun_NoReroute_MapsToSendFailed()
    {
        var sent = 0;
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true,
            send: _ => ++sent < 3); // 3rd chunk refused
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, vk },
            new FocusedChildCapture(7, true));

        var report = injector.TryInjectGuardedDetailed(new string('a', 80));

        report.Outcome.ShouldBe(InjectionRunOutcome.SendFailed); // existing pill flow
        report.Via.ShouldBe(DeliveryChannel.EmReplaceSel);
        report.ChunksSent.ShouldBe(2);   // strict prefix; remaining chunks unsent
        report.ChunksTotal.ShouldBe(10);
        em.Sent.Count.ShouldBe(3);       // the refused attempt was the last touch
        vk.Sent.ShouldBeEmpty();         // NO re-route to another rung mid-text
        vk.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void UnstableCapture_GatesOutRungs1And2_AndRecordsGates()
    {
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: _ => { },
            focusedChildCapture: _ => new FocusedChildCapture(0, false));
        // Default strategies: real EmReplaceSel/WmCharSmto gate out on the
        // zero effective hwnd; the default VkPacket wraps the sendChunk seam.
        var report = injector.TryInjectGuardedDetailed("hello world");

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        report.GatesSummary.ShouldBe("emReplaceSel:focus-unstable,wmCharSmto:focus-unstable");
    }

    [Fact]
    public void DefaultCapture_OffWindows_RoutesToVkPacket_PreservingStatusQuo()
    {
        if (OperatingSystem.IsWindows()) return; // Linux pin of the fallback path
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { }); // default capture + default strategies + default order

        var report = injector.TryInjectGuardedDetailed(new string('a', 16));

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        string.Concat(sent).ShouldBe(new string('a', 16));
    }

    [Fact]
    public void SettingsOrder_IsHonored_PerRun()
    {
        var em = new RecordingStrategy(DeliveryChannel.EmReplaceSel, canDeliver: true);
        var vk = new RecordingStrategy(DeliveryChannel.VkPacket, canDeliver: true);
        var injector = NewInjector(new IDeliveryStrategy[] { em, vk },
            new FocusedChildCapture(7, true),
            order: new[] { DeliveryChannel.VkPacket, DeliveryChannel.EmReplaceSel });

        injector.TryInjectGuardedDetailed("hi").Via.ShouldBe(DeliveryChannel.VkPacket);
        em.CanDeliverCalls.ShouldBe(0);
    }

    [Fact]
    public void EarlyParks_KeepDefaultViaAndNoGates()
    {
        // NoForeground park happens before routing: Via must read as the
        // default (VkPacket) and no gates record exists.
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 0,
            sendChunk: _ => true,
            sleep: _ => { });

        var report = injector.TryInjectGuardedDetailed("hello");

        report.Outcome.ShouldBe(InjectionRunOutcome.NoForeground);
        report.Via.ShouldBe(DeliveryChannel.VkPacket);
        report.GatesSummary.ShouldBeNullOrEmpty();
    }
}
