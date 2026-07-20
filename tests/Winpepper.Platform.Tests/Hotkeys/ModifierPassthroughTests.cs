using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: a key the user chose as a modifier in their hotkey must
/// keep working everywhere in Windows. The hook may observe the key and fire
/// its event, but it must never hide (swallow) a modifier key from the OS.
/// "Swallow" is the bool returned by <see cref="HotkeyHook.TryProcessKey"/>:
/// true hides the event from Windows / the foreground app; false lets it flow
/// through to the OS.
/// </summary>
public class ModifierPassthroughTests
{
    private static HotkeyHook NewHook(string hold,
                                       string toggle = "LeftAlt+F12",
                                       string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle),
               HotkeyChord.Parse(cancel), new NullLogger<HotkeyHook>());

    [Fact]
    public void HoldModifierOnlyChord_CompletingModifier_PassesThroughToWindows()
    {
        var hook = NewHook(hold: "RightCtrl+RightShift");

        // RightCtrl arms the chord and already passes through.
        hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();

        // RightShift completes the chord: the hold fires, but Shift must still
        // reach Windows so it keeps shifting system-wide.
        hook.TryProcessKey(VK_RSHIFT, down: true, out var down).ShouldBeFalse();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Release is symmetric: the Shift up also passes through.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeFalse();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleModifierOnlyChord_CompletingModifier_PassesThroughToWindows()
    {
        var hook = NewHook(hold: "LeftAlt+F12", toggle: "LeftCtrl+LeftShift");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();

        hook.TryProcessKey(VK_LSHIFT, down: true, out var evt).ShouldBeFalse();
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void MixedChord_ModifiersPassThrough_TriggerKeyIsSwallowed()
    {
        var hook = NewHook(hold: "LeftAlt+F12", toggle: "Ctrl+Shift+Space");

        // Every modifier of the chord flows through to Windows.
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();

        // Only the non-modifier trigger key (Space) is hidden, so pressing the
        // hotkey does not type a space into the focused app.
        hook.TryProcessKey(0x20 /* Space */, down: true, out var evt).ShouldBeTrue();
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);
        hook.TryProcessKey(0x20, down: false, out _).ShouldBeTrue();

        // Modifiers still release cleanly through to Windows.
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
    }
}
