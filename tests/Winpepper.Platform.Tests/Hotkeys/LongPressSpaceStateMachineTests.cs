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
        machine.Process(false, false).ShouldBeTrue();
    }

    [Fact]
    public void CancellationDuringHoldingEndsHoldButRetainsReleaseOwnership()
    {
        var (machine, timer, events, replays) = NewMachine();
        machine.Process(true, false);
        timer.Fire();

        machine.Cancel(replayPending: true);

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        replays.ShouldBeEmpty();
        machine.IsActive.ShouldBeTrue();
        machine.Process(false, false).ShouldBeTrue();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void CancellationDuringSuppressionRetainsReleaseOwnership()
    {
        var (machine, _, events, replays) = NewMachine();
        machine.Process(true, false);
        machine.CancelPendingForModifier().ShouldBeTrue();

        machine.Cancel(replayPending: true);

        events.ShouldBeEmpty();
        replays.ShouldBe(new[] { "Space" });
        machine.IsActive.ShouldBeTrue();
        machine.Process(false, false).ShouldBeTrue();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void DisposeDuringHoldingEmitsHoldUpAndClearsState()
    {
        var (machine, timer, events, replays) = NewMachine();
        machine.Process(true, false);
        timer.Fire();

        machine.Dispose();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        replays.ShouldBeEmpty();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task ThresholdAndReleaseCannotPublishHoldUpBeforeHoldDown()
    {
        var timer = new FakeTimer();
        var events = new List<HotkeyEventKind>();
        using var downEntered = new ManualResetEventSlim();
        using var allowDown = new ManualResetEventSlim();
        var machine = new LongPressSpaceStateMachine(timer, kind =>
        {
            if (kind == HotkeyEventKind.HoldDown)
            {
                downEntered.Set();
                allowDown.Wait(TestContext.Current.CancellationToken);
            }
            lock (events) events.Add(kind);
        }, () => { });
        machine.Process(true, false);

        var threshold = Task.Run(timer.Fire, TestContext.Current.CancellationToken);
        downEntered.Wait(TestContext.Current.CancellationToken);
        var release = Task.Run(() => machine.Process(false, false), TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        release.IsCompleted.ShouldBeFalse();
        allowDown.Set();
        await Task.WhenAll(threshold, release);

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
    }
}
