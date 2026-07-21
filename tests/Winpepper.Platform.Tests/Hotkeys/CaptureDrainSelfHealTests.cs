using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: while the settings UI captures a chord, the hook drains
/// (passes through) every key until the captured keys are released. If a
/// captured key's key-up is dropped by Windows, drain mode must NOT wedge
/// forever and silently kill all hotkeys - it must recover on the next key
/// event so the user's hold/toggle hotkeys keep working.
/// </summary>
public class CaptureDrainSelfHealTests
{
    private const int A = 0x41;

    private static HotkeyHook NewHook(Func<DateTimeOffset> now, Func<int, bool> physicallyDown)
        => new(HotkeyChord.Parse("RightCtrl+RightShift"),
               HotkeyChord.Parse("Ctrl+Shift+Space"),
               HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
               cancelEnabled: null, timeProvider: now, keyPhysicallyDown: physicallyDown);

    [Fact]
    public void CaptureDrain_LostKeyUp_RecoversOnNextEvent_HotkeysResume()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        hook.SetSuspended(true);

        // During capture the user holds LeftCtrl then A; both enter the drain set.
        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(A);           hook.TryProcessKey(A, down: true, out _).ShouldBeFalse();

        // LeftCtrl is released normally (clears its modifier); A's key-up is LOST.
        down.Remove(VK_LCONTROL);
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();
        hook.SetSuspended(false);
        down.Remove(A); // physically released, but no key-up event is delivered

        // Drain is wedged: _captureKeysDown still holds A. The hold chord must
        // still fire because the stale A entry self-heals on the next event.
        down.Add(VK_RCONTROL); hook.TryProcessKey(VK_RCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_RSHIFT);   hook.TryProcessKey(VK_RSHIFT, down: true, out var ev).ShouldBeFalse();
        ev!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
    }
}
