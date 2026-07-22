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
        List<HotkeyEventKind> Events) NewMachine(Func<bool>? physicallyDown = null,
        Func<bool>? canStartHold = null)
    {
        var timer = new FakeTimer();
        var events = new List<HotkeyEventKind>();
        return (new LongPressSpaceStateMachine(timer, events.Add,
            isSpacePhysicallyDown: physicallyDown, canStartHold: canStartHold), timer, events);
    }

    [Fact]
    public void ShortTapPassesOriginalDownAndUpWithoutEvents()
    {
        var (machine, timer, events) = NewMachine();

        machine.Process(down: true).ShouldBeFalse();
        timer.DueTime.ShouldBe(TimeSpan.FromMilliseconds(300));
        machine.Process(down: false).ShouldBeFalse();

        events.ShouldBeEmpty();
    }

    [Fact]
    public void ThresholdStartsHoldAndPhysicalReleasePassesThrough()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true).ShouldBeFalse();

        timer.Fire();
        events.ShouldBe(new[] { HotkeyEventKind.HoldDown });
        machine.Process(false).ShouldBeFalse();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
    }

    [Fact]
    public void FirstTypematicDownStartsHoldAndIsSuppressed()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true).ShouldBeFalse();

        machine.Process(true).ShouldBeTrue();
        events.ShouldBe(new[] { HotkeyEventKind.HoldDown });

        timer.Fire();
        events.ShouldBe(new[] { HotkeyEventKind.HoldDown });
    }

    [Fact]
    public void FurtherTypematicDownsAreSuppressedUntilPassedThroughRelease()
    {
        var (machine, _, events) = NewMachine();
        machine.Process(true).ShouldBeFalse();
        machine.Process(true).ShouldBeTrue();

        machine.Process(true).ShouldBeTrue();
        machine.Process(false).ShouldBeFalse();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void ModifierCancellationPassesRepeatsAndReleaseWithoutStartingHold()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true).ShouldBeFalse();

        machine.CancelPendingForModifier().ShouldBeTrue();
        machine.Process(true).ShouldBeFalse();
        timer.Fire();
        machine.Process(false).ShouldBeFalse();

        events.ShouldBeEmpty();
    }

    [Fact]
    public void CancellationWhilePendingPassesRemainderOfPhysicalPress()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true).ShouldBeFalse();

        machine.Cancel();
        machine.Process(true).ShouldBeFalse();
        timer.Fire();
        machine.Process(false).ShouldBeFalse();

        events.ShouldBeEmpty();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void CancellationWhileHoldingEndsHoldAndPassesRemainderOfPhysicalPress()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true);
        timer.Fire();

        machine.Cancel();
        machine.Process(true).ShouldBeFalse();
        machine.Process(false).ShouldBeFalse();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void LostReleaseWhilePendingDoesNotStartHold()
    {
        var physicallyDown = true;
        var (machine, timer, events) = NewMachine(() => physicallyDown);
        machine.Process(true).ShouldBeFalse();

        physicallyDown = false;
        timer.Fire();

        events.ShouldBeEmpty();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void ReadinessDisabledBeforeThresholdLeavesRemainderNative()
    {
        var enabled = true;
        var (machine, timer, events) = NewMachine(canStartHold: () => enabled);
        machine.Process(true).ShouldBeFalse();

        enabled = false;
        timer.Fire();
        machine.Process(true).ShouldBeFalse();
        machine.Process(false).ShouldBeFalse();

        events.ShouldBeEmpty();
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void LostReleaseWhileHoldingEmitsHoldUpDuringRecovery()
    {
        var physicallyDown = true;
        var (machine, timer, events) = NewMachine(() => physicallyDown);
        machine.Process(true);
        timer.Fire();

        physicallyDown = false;
        machine.RecoverIfReleased();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
        machine.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void DisposeDuringHoldingEmitsHoldUpAndClearsState()
    {
        var (machine, timer, events) = NewMachine();
        machine.Process(true);
        timer.Fire();

        machine.Dispose();

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
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
        });
        machine.Process(true);

        var threshold = Task.Run(timer.Fire, TestContext.Current.CancellationToken);
        downEntered.Wait(TestContext.Current.CancellationToken);
        var release = Task.Run(() => machine.Process(false), TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        release.IsCompleted.ShouldBeFalse();
        allowDown.Set();
        await Task.WhenAll(threshold, release);

        events.ShouldBe(new[] { HotkeyEventKind.HoldDown, HotkeyEventKind.HoldUp });
    }
}
