using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: Windows can silently drop the key-up for a swallowed hotkey
/// trigger (heavy ASR work exceeds LowLevelHooksTimeout). A dropped key-up must
/// NEVER leave that common key swallowed system-wide. The stale entry must
/// self-heal when the physical key is no longer held (GetAsyncKeyState), while
/// a key that is still physically held must remain live regardless of age.
/// </summary>
public class SwallowSelfHealTests
{
    // Space is the non-modifier trigger of the default Ctrl+Shift+Space toggle.
    private const int Space = 0x20;
    private const int A = 0x41;

    private static HotkeyHook NewHook(
        Func<DateTimeOffset> now,
        Func<int, bool> physicallyDown,
        string hold = "RightCtrl+RightShift",
        string toggle = "Ctrl+Shift+Space",
        string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle),
               HotkeyChord.Parse(cancel), new NullLogger<HotkeyHook>(),
               cancelEnabled: null, timeProvider: now, keyPhysicallyDown: physicallyDown);

    [Fact]
    public void SwallowedTrigger_LostKeyUp_PhysicalRelease_FreshPressPassesThrough()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        // Press the toggle chord. Space (the trigger) is swallowed.
        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_LSHIFT);   hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        down.Add(Space);       hook.TryProcessKey(Space, down: true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Space's key-up is NEVER delivered. The user physically releases Space
        // and both modifiers (their ups DO arrive - modifiers pass through).
        down.Remove(Space);
        down.Remove(VK_LSHIFT); hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        down.Remove(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        // The modifier-up events already ran the stale sweep and healed Space
        // (physically up). A fresh, standalone Space press must reach the app.
        down.Add(Space);
        hook.TryProcessKey(Space, down: true, out var fresh).ShouldBeFalse();
        fresh.ShouldBeNull();
    }

    [Fact]
    public void HeldTrigger_Autorepeat_StillSwallowed_WhilePhysicallyDown()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_LSHIFT);   hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        down.Add(Space);       hook.TryProcessKey(Space, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Genuine autorepeat: physically held. Keep swallowing and never
        // re-fire Toggle.
        for (var i = 0; i < 3; i++)
        {
            clock = clock.AddMilliseconds(40);
            hook.TryProcessKey(Space, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        down.Remove(Space);
        hook.TryProcessKey(Space, down: false, out _).ShouldBeTrue();
    }

    [Fact]
    public void HeldTrigger_DelayedAutorepeat_RemainsSwallowed_WhilePhysicallyDown()
    {
        var down = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        var hook = NewHook(now: () => clock, physicallyDown: down.Contains);

        down.Add(VK_LCONTROL); hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        down.Add(VK_LSHIFT);   hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        down.Add(Space);       hook.TryProcessKey(Space, down: true, out var first).ShouldBeTrue();
        first!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Filter Keys and other accessibility/typematic settings can delay the
        // first repeat past the stale-entry timeout. A confirmed-held key is
        // still an autorepeat and must not fire Toggle a second time.
        clock = clock.AddSeconds(2);

        hook.TryProcessKey(Space, down: true, out var repeat).ShouldBeTrue();
        repeat.ShouldBeNull();
    }
}
