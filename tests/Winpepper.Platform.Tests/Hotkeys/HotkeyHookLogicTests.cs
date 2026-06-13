using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyHookLogicTests
{
    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                       string toggle = "Ctrl+Shift+Space",
                                       string cancel = "Esc",
                                       Func<bool>? cancelEnabled = null)
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(), cancelEnabled);

    [Fact]
    public void HoldChord_PressAndRelease_EmitsHoldDownThenHoldUp()
    {
        var hook = NewHook();
        // Right Ctrl down, then Right Shift down should fire HoldDown.
        hook.TryProcessKey(VK_RCONTROL, down: true,  out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT,   down: true,  out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Releasing either modifier should fire HoldUp.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleChord_KeyDown_FiresToggleOnce()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT,   down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20 /*Space*/, down: true, out var ev).ShouldBeTrue();
        ev!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    // Regression tests for stuck modifiers (asymmetric swallowing).
    // The hook must only swallow a key-up when it swallowed that key's down;
    // otherwise the OS sees a down with no up and the key sticks.

    [Fact]
    public void HoldChord_ReleasingPassedThroughModifierFirst_EmitsHoldUpWithoutSwallowing()
    {
        var hook = NewHook();
        // RShift pressed first: its down does NOT complete the chord, so it
        // passes through to the system. RCtrl completes the chord (swallowed).
        hook.TryProcessKey(VK_RSHIFT,   down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RCONTROL, down: true, out var downEvt).ShouldBeTrue();
        downEvt!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Releasing RShift breaks the chord: HoldUp must fire, but the key-up
        // must NOT be swallowed — the system saw its key-down.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var upEvt).ShouldBeFalse();
        upEvt!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        // RCtrl's down was swallowed, so its up must be swallowed too.
        hook.TryProcessKey(VK_RCONTROL, down: false, out var none).ShouldBeTrue();
        none.ShouldBeNull();
    }

    [Fact]
    public void HoldChord_AutorepeatOfCompletingModifier_SwallowedWithoutDuplicateEvent()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT,   down: true, out var evt).ShouldBeTrue();
        evt!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Windows autorepeats the last-pressed key while the chord is held.
        // Repeats must stay swallowed (no leak to the foreground app) and must
        // not emit duplicate events.
        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(VK_RSHIFT, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleChord_Autorepeat_FiresToggleOnlyOnce()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT,   down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(0x20, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        // Space's down was swallowed; its up is swallowed too. The modifiers
        // passed through on the way down, so their ups pass through as well.
        hook.TryProcessKey(0x20, down: false, out _).ShouldBeTrue();
        hook.TryProcessKey(VK_LSHIFT,   down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void CancelChord_EscUp_PassesThrough()
    {
        var hook = NewHook();
        hook.TryProcessKey(0x1B, down: true,  out var ev).ShouldBeFalse();
        ev!.Kind.ShouldBe(HotkeyEventKind.Cancel);
        hook.TryProcessKey(0x1B, down: false, out var up).ShouldBeFalse();
        up.ShouldBeNull();
    }

    [Fact]
    public void CancelChord_PlainEsc_EmitsCancelWithoutSwallowing()
    {
        var hook = NewHook();
        hook.TryProcessKey(0x1B, down: true, out var ev).ShouldBeFalse();
        ev!.Kind.ShouldBe(HotkeyEventKind.Cancel);
    }

    [Fact]
    public void CancelChord_Autorepeat_EmitsCancelOnlyOnceWithoutSwallowing()
    {
        var hook = NewHook();

        hook.TryProcessKey(0x1B, down: true, out var first).ShouldBeFalse();
        first!.Kind.ShouldBe(HotkeyEventKind.Cancel);

        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(0x1B, down: true, out var repeat).ShouldBeFalse();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(0x1B, down: false, out var up).ShouldBeFalse();
        up.ShouldBeNull();
    }

    [Fact]
    public void CancelChord_WhenDisabled_PlainEscPassesThrough()
    {
        var hook = NewHook(cancelEnabled: () => false);

        hook.TryProcessKey(0x1B, down: true, out var down).ShouldBeFalse();
        down.ShouldBeNull();

        hook.TryProcessKey(0x1B, down: false, out var up).ShouldBeFalse();
        up.ShouldBeNull();
    }
}
