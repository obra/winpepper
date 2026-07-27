namespace Winpepper.Platform.Injection;

/// <summary>
/// Detects physically-held mouse buttons for the guarded-injection halt and
/// prelude logic. A click-to-switch focus change starts with a button going
/// DOWN before the foreground flips, so button-down is the earliest
/// observable leading edge of a click halt gesture -- the mouse analogue of
/// the modifier check (Alt is down before Alt+Tab flips the foreground).
///
/// Deliberately SEPARATE from <see cref="ModifierGuard.ModifierVks"/>: that
/// set also drives the neutralization prelude, which synthesizes keyboard
/// KEYUPs on timeout. There is no safe mouse analogue (synthesizing a mouse
/// button-up would fabricate a click), so mouse buttons are only ever
/// OBSERVED, never synthesized.
///
/// VK_LBUTTON/VK_RBUTTON are LOGICAL buttons (Windows applies the user's
/// swap-buttons setting); this checks the union, so the swap is irrelevant.
/// Pure managed; the probe is injectable and fail-open: a probe that cannot
/// observe reports "up", so a failed observation never halts a paste.
/// </summary>
public static class MouseButtonGuard
{
    /// <summary>VK_LBUTTON, VK_RBUTTON, VK_MBUTTON.</summary>
    public static readonly int[] MouseButtonVks = { 0x01, 0x02, 0x04 };

    /// <summary>Whether any mouse button is reported down by the probe.</summary>
    public static bool AnyDown(Func<int, bool> isKeyDown)
    {
        foreach (var vk in MouseButtonVks)
            if (isKeyDown(vk)) return true;
        return false;
    }
}
