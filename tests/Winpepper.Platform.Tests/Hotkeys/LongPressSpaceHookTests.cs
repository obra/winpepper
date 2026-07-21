using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class LongPressSpaceHookTests
{
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

    private static HotkeyHook NewHook(FakeTimer timer, Action replay)
        => new(HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true,
            spaceTimerScheduler: timer, replaySpace: replay);

    [Fact]
    public void SpacePolicy_EmitsHoldEventsThroughHookChannel()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer, () => { });

        hook.TryProcessKey(0x20, true, out var immediate).ShouldBeTrue();
        immediate.ShouldBeNull();
        hook.TryProcessKey(0x20, true, out var repeat).ShouldBeTrue();
        repeat.ShouldBeNull();

        timer.Fire();
        hook.Events.TryRead(out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.TryProcessKey(0x20, false, out _).ShouldBeTrue();
        hook.Events.TryRead(out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void OwnReplayMarkerPassesThroughWithoutReenteringSpacePolicy()
    {
        var hook = NewHook(new FakeTimer(), () => { });

        hook.TryProcessKey(0x20, true, out var down,
            extraInfo: HotkeyHook.SpaceReplayExtraInfo).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out var up,
            extraInfo: HotkeyHook.SpaceReplayExtraInfo).ShouldBeFalse();

        down.ShouldBeNull();
        up.ShouldBeNull();
    }

    [Fact]
    public void ReconfigurationReplaysPendingShortTap()
    {
        var replayCount = 0;
        var hook = NewHook(new FakeTimer(), () => replayCount++);
        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();

        hook.UpdateChords(HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"));

        replayCount.ShouldBe(1);
    }
}
