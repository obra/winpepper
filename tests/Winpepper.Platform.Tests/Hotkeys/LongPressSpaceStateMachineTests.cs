using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class LongPressSpaceStateMachineTests
{
    private sealed class FakeTimer : ILongPressTimerScheduler
    {
        private Action? _callback;
        public TimeSpan? DueTime { get; private set; }

        public IDisposable Schedule(TimeSpan dueTime, Action callback)
        {
            DueTime = dueTime;
            _callback = callback;
            return new Cancellation(() => _callback = null);
        }

        public void Fire() => _callback?.Invoke();

        private sealed class Cancellation(Action cancel) : IDisposable
        {
            private Action? _cancel = cancel;
            public void Dispose() => Interlocked.Exchange(ref _cancel, null)?.Invoke();
        }
    }

    private static (LongPressSpaceStateMachine Machine, FakeTimer Timer,
        List<HotkeyEventKind> Events, List<string> Replays) NewMachine()
    {
        var timer = new FakeTimer();
        var events = new List<HotkeyEventKind>();
        var replays = new List<string>();
        return (new LongPressSpaceStateMachine(timer, events.Add, () => replays.Add("Space")),
            timer, events, replays);
    }

    [Fact]
    public void ShortTap_IsSwallowedAndReplaysExactlyOneSpace()
    {
        var (machine, timer, events, replays) = NewMachine();

        machine.Process(down: true, isOwnReplay: false).ShouldBeTrue();
        timer.DueTime.ShouldBe(TimeSpan.FromMilliseconds(300));
        machine.Process(down: false, isOwnReplay: false).ShouldBeTrue();

        replays.ShouldBe(new[] { "Space" });
        events.ShouldBeEmpty();
    }

    [Fact]
    public void ThresholdStartsHold_AndReleaseStopsWithoutReplay()
    {
        var (machine, timer, events, replays) = NewMachine();
        machine.Process(true, false).ShouldBeTrue();

        timer.Fire();
        events.ShouldBe(new[] { HotkeyEventKind.HoldDown });
        machine.Process(false, false).ShouldBeTrue();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        replays.ShouldBeEmpty();
    }

    [Fact]
    public void TypematicDownsAreSwallowedAndDoNotRestartThreshold()
    {
        var (machine, timer, events, _) = NewMachine();
        machine.Process(true, false).ShouldBeTrue();
        var due = timer.DueTime;

        machine.Process(true, false).ShouldBeTrue();
        timer.DueTime.ShouldBe(due);
        timer.Fire();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown });
    }

    [Fact]
    public void OwnInjectedReplayPassesThroughWithoutChangingState()
    {
        var (machine, _, events, replays) = NewMachine();

        machine.Process(true, isOwnReplay: true).ShouldBeFalse();
        machine.Process(false, isOwnReplay: true).ShouldBeFalse();

        events.ShouldBeEmpty();
        replays.ShouldBeEmpty();
    }

    [Fact]
    public void CancellationReplaysPendingTapButEndsActiveHold()
    {
        var (pending, _, pendingEvents, pendingReplays) = NewMachine();
        pending.Process(true, false);
        pending.Cancel(replayPending: true);
        pendingEvents.ShouldBeEmpty();
        pendingReplays.ShouldBe(new[] { "Space" });

        var (holding, timer, holdingEvents, holdingReplays) = NewMachine();
        holding.Process(true, false);
        timer.Fire();
        holding.Cancel(replayPending: true);
        holdingEvents.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        holdingReplays.ShouldBeEmpty();
    }

    [Fact]
    public void CancellationCanDiscardPendingTapDuringExplicitShutdown()
    {
        var (machine, timer, events, replays) = NewMachine();
        machine.Process(true, false);

        machine.Cancel(replayPending: false);
        timer.Fire();

        events.ShouldBeEmpty();
        replays.ShouldBeEmpty();
    }
}
