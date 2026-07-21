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
        // These tests synthesize key events; never consult the host keyboard.
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(), cancelEnabled,
               keyPhysicallyDown: _ => true);

    [Fact]
    public void HoldChord_PressAndRelease_EmitsHoldDownThenHoldUp()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void NonModifierHoldTrigger_OwnRelease_EmitsHoldUp()
    {
        var hook = NewHook(hold: "F24");

        hook.TryProcessKey(0x87, down: true, out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.TryProcessKey(0x87, down: false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void NonModifierHoldTrigger_RepeatDoesNotEmitAnotherHoldDown()
    {
        var hook = NewHook(hold: "F24");

        hook.TryProcessKey(0x87, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
        hook.TryProcessKey(0x87, down: true, out var repeat).ShouldBeTrue();
        repeat.ShouldBeNull();
        hook.TryProcessKey(0x87, down: false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact]
    public void ToggleChord_KeyDown_FiresToggleOnce()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20 /* Space */, down: true, out var ev).ShouldBeTrue();
        ev!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void HoldChord_ReleasingPassedThroughModifierFirst_EmitsHoldUpWithoutSwallowing()
    {
        var hook = NewHook();
        // RShift pressed first: its down passes through. RCtrl completes the
        // chord; it is a modifier, so it now also passes through.
        hook.TryProcessKey(VK_RSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RCONTROL, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Both key-downs reached the system, so both key-ups pass through too.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out var none).ShouldBeFalse();
        none.ShouldBeNull();
    }

    [Fact]
    public void HoldChord_AutorepeatOfCompletingModifier_PassesThroughWithoutDuplicateEvent()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var evt).ShouldBeFalse();
        evt!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Autorepeat of the held modifier keeps passing through and must not
        // re-fire HoldDown (ActivatesOnKeyDown only fires on the
        // incomplete->complete transition).
        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(VK_RSHIFT, down: true, out var repeat).ShouldBeFalse();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleChord_Autorepeat_FiresToggleOnlyOnce()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(0x20, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(0x20, down: false, out _).ShouldBeTrue();
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ModifierOnlyToggle_UnrelatedKeyWhileHeld_DoesNotToggleAgain()
    {
        var hook = NewHook(toggle: "LeftCtrl+LeftShift");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var toggle).ShouldBeFalse();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        hook.TryProcessKey(0x41 /* A */, down: true, out var unrelated).ShouldBeFalse();
        unrelated.ShouldBeNull();
        hook.TryProcessKey(0x41, down: false, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void UpdateChords_ReplacesActiveHoldChord()
    {
        var hook = NewHook();
        hook.UpdateChords(HotkeyChord.Parse("LeftCtrl+LeftShift"), HotkeyChord.Parse("LeftAlt+F12"));

        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT, down: true, out var oldChord).ShouldBeFalse();
        oldChord.ShouldBeNull();
        hook.TryProcessKey(VK_RSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var newChord).ShouldBeFalse();
        newChord!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }

    [Fact]
    public void SuspendedHook_PassesCaptureChordThrough_AndResumesAfterward()
    {
        var hook = NewHook(hold: "LeftCtrl+LeftShift");
        hook.SetSuspended(true);

        hook.TryProcessKey(VK_LCONTROL, down: true, out var first).ShouldBeFalse();
        first.ShouldBeNull();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var second).ShouldBeFalse();
        second.ShouldBeNull();
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        hook.SetSuspended(false);
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out var resumed).ShouldBeFalse();
        resumed!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }

    [Fact]
    public void ResumeAfterCapture_WaitsForCapturedKeysAndRepeatsToBeReleased()
    {
        var hook = NewHook();
        hook.SetSuspended(true);

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x41 /* A */, down: true, out _).ShouldBeFalse();

        hook.UpdateChords(
            HotkeyChord.Parse("LeftCtrl+LeftShift+A"),
            HotkeyChord.Parse("LeftAlt+F12"));
        hook.SetSuspended(false);

        for (var i = 0; i < 3; i++)
        {
            hook.TryProcessKey(0x41, down: true, out var repeat).ShouldBeFalse();
            repeat.ShouldBeNull();
        }

        hook.TryProcessKey(0x41, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x41, down: true, out var fresh).ShouldBeTrue();
        fresh!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }

    [Fact]
    public void CancelChord_EscUp_PassesThrough()
    {
        var hook = NewHook();
        hook.TryProcessKey(0x1B, down: true, out var ev).ShouldBeFalse();
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
