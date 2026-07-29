using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// Pins the injected-event fast-path (standard WH_KEYBOARD_LL practice,
/// 2026-07-28): synthetic events (LLKHF_INJECTED) pass straight through --
/// they never match chords, never mutate key-state tracking, and are never
/// swallowed. Winpepper's own injection stream (KEYEVENTF_UNICODE text plus
/// NeutralizeHeldModifiers KEYUPs) is the dominant producer; the fast-path
/// removes the hook's per-event tax (~0.2 ms/event measured on the
/// production host) from every injected keystroke system-wide. The chord
/// recorder still receives injected transitions and filters them itself.
/// </summary>
public class InjectedEventFastPathTests
{
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_LWIN = 0x5B;
    private const int VK_SPACE = 0x20;
    private const int VK_PACKET = 0xE7; // KEYEVENTF_UNICODE arrives as VK_PACKET

    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                      string toggle = "Ctrl+Shift+Space",
                                      string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true);

    [Fact] // RED before the fast-path
    public void InjectedWinKeyUp_DuringActiveHold_DoesNotEndTheHold()
    {
        // NeutralizeHeldModifiers sends KEYEVENTF_KEYUP for physically-held
        // modifiers, including generic VK_LWIN/VK_RWIN. Before the
        // fast-path, that injected KEYUP cleared _modifiers (the event-
        // stream fold) and ended a Win-containing hold chord spuriously
        // while the key was still physically down.
        var hook = NewHook(hold: "Ctrl+Win");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LWIN, down: true, out var holdDown).ShouldBeFalse();
        holdDown!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Our own neutralization KEYUP: must be ignored.
        hook.TryProcessKey(VK_LWIN, down: false, out var injectedUp, isInjected: true)
            .ShouldBeFalse();
        injectedUp.ShouldBeNull();

        // The PHYSICAL release still ends the hold, exactly once.
        hook.TryProcessKey(VK_LWIN, down: false, out var physicalUp).ShouldBeFalse();
        physicalUp!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact] // RED before the fast-path
    public void InjectedChord_DoesNotFireToggle_AndIsNeverSwallowed()
    {
        // Synthetic input (any process's SendInput) must not trigger
        // hotkeys and must never be swallowed.
        var hook = NewHook();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _, isInjected: true).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _, isInjected: true).ShouldBeFalse();
        hook.TryProcessKey(VK_SPACE, down: true, out var evt, isInjected: true).ShouldBeFalse();

        evt.ShouldBeNull();
    }

    [Fact] // RED before the fast-path
    public void InjectedSpaceDown_DoesNotStartALongPressSpaceHold()
    {
        var admissions = 0;
        var hook = new HotkeyHook(HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"),
            HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
            keyPhysicallyDown: _ => true,
            beforeLongPressSpaceAdmission: () => admissions++);

        hook.TryProcessKey(VK_SPACE, down: true, out var evt, isInjected: true).ShouldBeFalse();

        evt.ShouldBeNull();
        admissions.ShouldBe(0);
    }

    [Fact] // pin: passes before AND after -- makes the accidental inertness deliberate
    public void InjectedGenericModifierKeyUps_DoNotDisturbPhysicalModifierState()
    {
        // NeutralizeHeldModifiers sends generic VKs {0x10, 0x11, 0x12, 0x5B,
        // 0x5C}. None of them may perturb the fold over PHYSICAL events: a
        // physically-completed toggle chord must still fire afterwards.
        var hook = NewHook();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _);
        hook.TryProcessKey(VK_LSHIFT, down: true, out _);

        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            hook.TryProcessKey(vk, down: false, out _, isInjected: true).ShouldBeFalse();

        hook.TryProcessKey(VK_SPACE, down: true, out var evt).ShouldBeTrue(); // toggle trigger key swallowed
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact] // pin: passes before AND after
    public void InjectedUnicodeTextStream_PassesThrough_WithNoEvents()
    {
        // The paste stream: one 8-code-unit chunk = 16 injected VK_PACKET events.
        var hook = NewHook();

        for (var i = 0; i < 8; i++)
        {
            hook.TryProcessKey(VK_PACKET, down: true, out var d, isInjected: true).ShouldBeFalse();
            d.ShouldBeNull();
            hook.TryProcessKey(VK_PACKET, down: false, out var u, isInjected: true).ShouldBeFalse();
            u.ShouldBeNull();
        }
    }

    [Fact] // pin: passes before AND after -- the raw-capture contract stays live
    public void RawCapture_StillReceivesInjectedTransitions_MarkedInjected()
    {
        // ChordRecorder filters injected transitions ITSELF
        // (ChordRecorder.OnRawKey ignores IsInjected), so the hook must keep
        // forwarding them to an active sink.
        var hook = NewHook();
        var seen = new List<RawKeyTransition>();
        using var lease = hook.BeginRawCapture(seen.Add);

        hook.TryProcessKey(0x41, down: true, out _, scanCode: 30, isInjected: true)
            .ShouldBeFalse();

        seen.Count.ShouldBe(1);
        seen[0].VirtualKey.ShouldBe(0x41);
        seen[0].IsDown.ShouldBeTrue();
        seen[0].IsInjected.ShouldBeTrue();
    }
}
