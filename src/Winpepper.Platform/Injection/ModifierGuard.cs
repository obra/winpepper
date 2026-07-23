namespace Winpepper.Platform.Injection;

/// <summary>
/// Guards text injection against physically-held modifier keys. Injected
/// Unicode packets are delivered with the CURRENT physical modifier state
/// applied, so a user still holding Ctrl (e.g. the dictation hotkey chord, or
/// Ctrl held while clicking the pending-paste pill) turns every injected
/// character into a control character / accelerator in the target app.
///
/// Strategy (the text-expander standard — AutoHotkey/espanso do the same):
///  1. Wait briefly for the user to physically release all modifiers
///     (they nearly always do within a few hundred ms).
///  2. If they keep holding past the timeout, synthesize KEYUP events for the
///     held modifiers — releases ONLY, never re-press, so the user's eventual
///     physical release becomes a harmless no-op and no key is ever stuck.
///
/// The wait/probe core is pure (injectable probe + sleep) and unit-tested on
/// Linux; only the SendInput plumbing is Windows-specific.
/// </summary>
public static class ModifierGuard
{
    /// <summary>Shift, Ctrl, Alt (VK_MENU), LWin, RWin.</summary>
    public static readonly int[] ModifierVks = { 0x10, 0x11, 0x12, 0x5B, 0x5C };

    /// <summary>Whether any modifier is reported down by the probe.</summary>
    public static bool AnyDown(Func<int, bool> isKeyDown)
    {
        foreach (var vk in ModifierVks)
            if (isKeyDown(vk)) return true;
        return false;
    }

    /// <summary>The modifier VKs currently reported down by the probe.</summary>
    public static IReadOnlyList<int> HeldModifiers(Func<int, bool> isKeyDown)
    {
        var held = new List<int>();
        foreach (var vk in ModifierVks)
            if (isKeyDown(vk)) held.Add(vk);
        return held;
    }

    /// <summary>
    /// Poll until no modifier is down or the timeout elapses. Returns true when
    /// the keys were released in time; false when the timeout expired with a
    /// modifier still held. Sleep is injectable for tests.
    /// </summary>
    public static bool WaitForRelease(Func<bool> anyDown, int timeoutMs, int pollMs, Action<int> sleep)
    {
        if (timeoutMs < 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        if (pollMs <= 0) throw new ArgumentOutOfRangeException(nameof(pollMs));

        if (!anyDown()) return true;
        var waited = 0;
        while (waited < timeoutMs)
        {
            sleep(pollMs);
            waited += pollMs;
            if (!anyDown()) return true;
        }
        return false;
    }

    /// <summary>KEYUP inputs (VK-based, not Unicode) for the given modifiers.</summary>
    internal static SendInputNative.INPUT[] BuildKeyUpInputs(IReadOnlyList<int> vks)
    {
        var inputs = new SendInputNative.INPUT[vks.Count];
        for (var i = 0; i < vks.Count; i++)
        {
            inputs[i] = new SendInputNative.INPUT
            {
                Type = SendInputNative.INPUT_KEYBOARD,
                Keyboard = new SendInputNative.KEYBDINPUT
                {
                    Vk = (ushort)vks[i],
                    Scan = 0,
                    Flags = SendInputNative.KEYEVENTF_KEYUP,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };
        }
        return inputs;
    }
}
