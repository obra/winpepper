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
                                       string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>());

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

    [Fact]
    public void CancelChord_PlainEsc_Fires()
    {
        var hook = NewHook();
        hook.TryProcessKey(0x1B, down: true, out var ev).ShouldBeTrue();
        ev!.Kind.ShouldBe(HotkeyEventKind.Cancel);
    }
}
