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
/// self-heal - either because the physical key is no longer held
/// (GetAsyncKeyState) or because it outlived the bounded StaleKeyTimeout - so a
/// fresh press of that key reaches the app.
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
    public void SwallowedTrigger_LostKeyUp_HealedByExpiry_FreshPressPassesThrough()
    {
        var clock = DateTimeOffset.UtcNow;
        // Physical probe always reports DOWN, so healing here must come from the
        // bounded StaleKeyTimeout expiry alone.
        var hook = NewHook(now: () => clock, physicallyDown: _ => true);

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(Space, down: true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);

        // Space up lost; modifiers released so a later Space is a bare press.
        hook.TryProcessKey(VK_LSHIFT, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LCONTROL, down: false, out _).ShouldBeFalse();

        // Advance past the bounded stale window. The stale Space entry expires.
        clock = clock.AddSeconds(2);

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

        // Genuine autorepeat: physically held, well within StaleKeyTimeout. Keep
        // swallowing and never re-fire Toggle.
        for (var i = 0; i < 3; i++)
        {
            clock = clock.AddMilliseconds(40);
            hook.TryProcessKey(Space, down: true, out var repeat).ShouldBeTrue();
            repeat.ShouldBeNull();
        }

        down.Remove(Space);
        hook.TryProcessKey(Space, down: false, out _).ShouldBeTrue();
    }
}
