using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class LongPressSpaceHookTests
{
    private sealed class RecordingLogger : ILogger<HotkeyHook>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
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

    private static HotkeyHook NewHook(FakeTimer timer, Func<SpaceReplayResult> replay)
        => new(HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true,
            spaceTimerScheduler: timer, replaySpace: replay);

    [Fact]
    public void SpacePolicy_EmitsHoldEventsThroughHookChannel()
    {
        var timer = new FakeTimer();
        var hook = NewHook(timer, () => SpaceReplayResult.Succeeded);

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
        var hook = NewHook(new FakeTimer(), () => SpaceReplayResult.Succeeded);

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
        var hook = NewHook(new FakeTimer(), () => { replayCount++; return SpaceReplayResult.Succeeded; });
        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();

        hook.UpdateChords(HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"));

        replayCount.ShouldBe(1);
        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();
        hook.TryProcessKey(0x20, false, out _).ShouldBeTrue();
    }

    [Fact]
    public void ModifiedSpaceCanStillActivateConfiguredToggle()
    {
        var hook = new HotkeyHook(
            HotkeyChord.Parse("Space"), HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
            keyPhysicallyDown: _ => true,
            spaceTimerScheduler: new FakeTimer(),
            replaySpace: () => SpaceReplayResult.Succeeded);

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
        var hook = NewHook(timer, () => SpaceReplayResult.Succeeded);

        hook.TryProcessKey(0xA2, true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, true, out var down).ShouldBeFalse();
        hook.TryProcessKey(0x20, false, out var up).ShouldBeFalse();
        timer.Fire();

        down.ShouldBeNull();
        up.ShouldBeNull();
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void ModifierPressedDuringPendingTapReplaysBareSpaceAndSuppressesPhysicalRelease()
    {
        var timer = new FakeTimer();
        var replayCount = 0;
        var hook = NewHook(timer, () => { replayCount++; return SpaceReplayResult.Succeeded; });
        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();

        hook.TryProcessKey(0xA2, true, out var modifier).ShouldBeFalse();
        modifier.ShouldBeNull();
        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();
        hook.TryProcessKey(0x20, false, out _).ShouldBeTrue();
        timer.Fire();

        replayCount.ShouldBe(1);
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void ReplayFailureIsLoggedWithUipiContext()
    {
        var logger = new RecordingLogger();
        var hook = new HotkeyHook(
            HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            logger, keyPhysicallyDown: _ => true,
            spaceTimerScheduler: new FakeTimer(),
            replaySpace: () => new SpaceReplayResult(false, 0, false, false));

        hook.TryProcessKey(0x20, true, out _).ShouldBeTrue();
        hook.TryProcessKey(0x20, false, out _).ShouldBeTrue();

        logger.Messages.ShouldContain(message => message.Contains("UIPI", StringComparison.Ordinal));
    }
}
