using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class LongPressSpaceHookTests
{
    private sealed class OneShotBarrier(CancellationToken cancellationToken) : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _armed = 1;

        public void Block()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            _entered.Set();
            _release.Wait(cancellationToken);
        }

        public void WaitUntilEntered() => _entered.Wait(cancellationToken);
        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class FakeTimer : ILongPressTimerScheduler
    {
        private Action? _callback;
        public IDisposable Schedule(TimeSpan dueTime, Action callback)
        {
            _callback = callback;
            return new Cancellation(() => _callback = null);
        }
        public void Fire() => _callback?.Invoke();
        private sealed class Cancellation(Action cancel) : IDisposable
        {
            public void Dispose() => cancel();
        }
    }

    private static HotkeyHook NewHook(FakeTimer timer,
        Func<int, bool>? keyPhysicallyDown = null,
        Func<bool>? normalTriggersEnabled = null,
        Action? beforeLongPressSpaceAdmission = null)
        => new(HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>(), keyPhysicallyDown: keyPhysicallyDown ?? (_ => true),
            spaceTimerScheduler: timer, normalTriggersEnabled: normalTriggersEnabled,
            beforeLongPressSpaceAdmission: beforeLongPressSpaceAdmission);

    [Fact]
    public async Task StaleSpaceCallbackCannotStartAfterReconfigurationCompletes()
    {
        var timer = new FakeTimer();
        using var barrier = new OneShotBarrier(TestContext.Current.CancellationToken);
        var hook = NewHook(timer, beforeLongPressSpaceAdmission: barrier.Block);
        var callback = Task.Run(
            () => hook.TryProcessKey(0x20, true, out _),
            TestContext.Current.CancellationToken);
        barrier.WaitUntilEntered();

        hook.UpdateChords(HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"));
        barrier.Release();
        (await callback).ShouldBeFalse();
        timer.Fire();

        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StaleSpaceCallbackCannotStartAfterSuspensionCompletes()
    {
        var timer = new FakeTimer();
        using var barrier = new OneShotBarrier(TestContext.Current.CancellationToken);
        var hook = NewHook(timer, beforeLongPressSpaceAdmission: barrier.Block);
        var callback = Task.Run(
            () => hook.TryProcessKey(0x20, true, out _),
            TestContext.Current.CancellationToken);
        barrier.WaitUntilEntered();

        hook.SetSuspended(true);
        barrier.Release();
        (await callback).ShouldBeFalse();
        timer.Fire();

        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StaleSpaceCallbackCannotStartAfterRawCaptureBegins()
    {
        var timer = new FakeTimer();
        using var barrier = new OneShotBarrier(TestContext.Current.CancellationToken);
        var hook = NewHook(timer, beforeLongPressSpaceAdmission: barrier.Block);
        var callback = Task.Run(
            () => hook.TryProcessKey(0x20, true, out _),
            TestContext.Current.CancellationToken);
        barrier.WaitUntilEntered();

        using var capture = hook.BeginRawCapture(_ => { });
        barrier.Release();
        (await callback).ShouldBeFalse();
        timer.Fire();

        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void ReadinessEnabledDuringHeldSpaceDefersObservationUntilNextPress()
    {
        var enabled = false;
        var timer = new FakeTimer();
        var hook = NewHook(timer, normalTriggersEnabled: () => enabled);

        hook.TryProcessKey(0x20, true, out var disabledDown).ShouldBeFalse();
        enabled = true;
        hook.TryProcessKey(0x20, true, out var repeat).ShouldBeFalse();
        timer.Fire();
        hook.TryProcessKey(0x20, false, out var disabledUp).ShouldBeFalse();
        disabledDown.ShouldBeNull();
        repeat.ShouldBeNull();
        disabledUp.ShouldBeNull();
        hook.Events.TryRead(out _).ShouldBeFalse();

        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out var holdDown).ShouldBeTrue();
        holdDown!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
        hook.Events.TryRead(out var holdUp).ShouldBeTrue();
        holdUp!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void ShortTapPassesOriginalDownAndUpWithoutHoldEvents()
    {
        var hook = NewHook(new FakeTimer());

        hook.TryProcessKey(0x20, true, out var down).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out var up).ShouldBeFalse();

        down.ShouldBeNull();
        up.ShouldBeNull();
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void TimerStartsHoldWhilePhysicalDownAndUpAlwaysPassesThrough()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);

        hook.TryProcessKey(0x20, true, out var immediate).ShouldBeFalse();
        immediate.ShouldBeNull();
        timer.Fire();
        hook.Events.TryRead(out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void FirstAndFurtherTypematicDownsAreSuppressedWhileHolding()
    {
        var hook = NewHook(new FakeTimer());
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();

        hook.TryProcessKey(0x20, true, out var firstRepeat).ShouldBeTrue();
        firstRepeat.ShouldBeNull();
        hook.Events.TryRead(out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
        hook.TryProcessKey(0x20, true, out var secondRepeat).ShouldBeTrue();
        secondRepeat.ShouldBeNull();

        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void ReconfigurationWhilePendingPassesRemainderOfPress()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();

        hook.UpdateChords(HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"));

        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void ReconfigurationDuringHoldingEndsHoldAndPhysicalReleasePasses()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out _).ShouldBeTrue();

        hook.UpdateChords(HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"));

        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
    }

    [Fact]
    public void SuspensionDuringHoldingEndsHoldAndPhysicalReleasePasses()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out _).ShouldBeTrue();

        hook.SetSuspended(true);

        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
    }

    [Fact]
    public void RawCaptureDuringHoldingEndsHoldAndPhysicalReleasePasses()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out _).ShouldBeTrue();

        using var capture = hook.BeginRawCapture(_ => { });

        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
    }

    [Fact]
    public void LostReleaseWhilePendingDoesNotStartHold()
    {
        var spaceDown = true;
        var timer = new FakeTimer();
        var hook = NewHook(timer, vk => vk != 0x20 || spaceDown);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();

        spaceDown = false;
        timer.Fire();

        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void LostReleaseWhileHoldingEmitsHoldUpOnNextKeyEvent()
    {
        var spaceDown = true;
        var timer = new FakeTimer();
        var hook = NewHook(timer, vk => vk != 0x20 || spaceDown);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        timer.Fire();
        hook.Events.TryRead(out _).ShouldBeTrue();

        spaceDown = false;
        hook.TryProcessKey(0x41, true, out _).ShouldBeFalse();

        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void NewSpaceDownAfterLostUpRecoversBeforeObservingFreshPress()
    {
        var spaceDown = true;
        var timer = new FakeTimer();
        var hook = NewHook(timer, vk => vk != 0x20 || spaceDown);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        spaceDown = false;

        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        spaceDown = true;
        timer.Fire();

        hook.Events.TryRead(out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }

    [Fact]
    public void ModifiedSpaceCanStillActivateConfiguredToggle()
    {
        var hook = new HotkeyHook(
            HotkeyChord.Parse("Space"), HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
            keyPhysicallyDown: _ => true,
            spaceTimerScheduler: new FakeTimer());

        hook.TryProcessKey(0xA2, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0xA0, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);
        hook.TryProcessKey(0x20, false, out _).ShouldBeTrue();
    }

    [Fact]
    public void OtherModifiedSpaceShortcutPassesThrough()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);

        hook.TryProcessKey(0xA2, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, true, out var down).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out var up).ShouldBeFalse();
        timer.Fire();

        down.ShouldBeNull();
        up.ShouldBeNull();
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void ModifierPressedDuringPendingSpacePassesRemainderWithoutHold()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer);
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();

        hook.TryProcessKey(0xA2, true, out var modifier).ShouldBeFalse();
        modifier.ShouldBeNull();
        hook.TryProcessKey(0x20, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out _).ShouldBeFalse();
        timer.Fire();

        hook.Events.TryRead(out _).ShouldBeFalse();
    }
}
