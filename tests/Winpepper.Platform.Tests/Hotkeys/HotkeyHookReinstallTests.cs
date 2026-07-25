using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// A resume must leave NO half-tracked chord behind. Windows silently removes
/// the low-level hook across suspend/resume, so the transitions that would have
/// closed a chord (the key-ups) are simply never delivered; without a reset the
/// hook would think a chord is still held.
/// <para>
/// One case is NOT a silent drop: an ACTIVE hold owns an in-flight dictation,
/// so the reset must emit the terminating HoldUp itself - otherwise the session
/// never stops and every later press is swallowed by the engine's Idle guard.
/// </para>
/// </summary>
public class HotkeyHookReinstallTests
{
    private const int F24 = 0x87;

    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                      string toggle = "Ctrl+Shift+Space",
                                      string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(),
               keyPhysicallyDown: _ => true);

    [Fact]
    public void Reinstall_During_A_Hold_Emits_Exactly_One_Terminating_HoldUp()
    {
        var hook = NewHook(hold: "F24");
        hook.TryProcessKey(F24, true, out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.RequestHookReinstall(); // system resumed mid-dictation

        // The reset MUST close the hold itself. The physical key-up that would
        // have ended this dictation was eaten by the suspend, so without this
        // event SessionEngine stays in Recording forever - mic open, buffer
        // growing, every later HoldDown dropped by the State != Idle guard.
        hook.Events.TryRead(out var terminating).ShouldBeTrue();
        terminating!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        // ...and exactly once: a late physical up finds no tracked hold, so it
        // is neither swallowed nor turned into a second HoldUp.
        hook.TryProcessKey(F24, false, out var up).ShouldBeFalse();
        up.ShouldBeNull();
        hook.Events.TryRead(out _).ShouldBeFalse();
    }

    [Fact]
    public void Reinstall_Clears_Modifier_State_So_A_HalfHeld_Chord_Cannot_Complete()
    {
        var hook = NewHook(); // hold = RightCtrl+RightShift
        hook.TryProcessKey(VK_RCONTROL, true, out var first).ShouldBeFalse();
        first.ShouldBeNull(); // chord incomplete so far

        hook.RequestHookReinstall();

        // Without the reset, RightCtrl would still be "held" and this would
        // complete the chord and fire HoldDown.
        hook.TryProcessKey(VK_RSHIFT, true, out var afterResume).ShouldBeFalse();
        afterResume.ShouldBeNull();
    }

    [Fact]
    public void Hook_Still_Works_After_Reinstall()
    {
        var hook = NewHook(hold: "F24", toggle: "F23");

        hook.RequestHookReinstall();

        // Nothing was held, so the reset must emit NOTHING: the terminating
        // HoldUp is for an interrupted hold only, never a blanket stop event.
        hook.Events.TryRead(out _).ShouldBeFalse();

        hook.TryProcessKey(F24, true, out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
        hook.TryProcessKey(F24, false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        hook.TryProcessKey(0x86, true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void Reinstall_On_A_Never_Started_Hook_Is_Safe()
    {
        var hook = NewHook();

        Should.NotThrow(() => hook.RequestHookReinstall());
        Should.NotThrow(() => hook.Dispose());
    }
}
