namespace Winpepper.Platform.Injection;

/// <summary>Per-chunk continue/halt outcome while a paste is in flight.</summary>
public enum MidPasteDecision
{
    /// <summary>Same window positively still foreground: keep typing.</summary>
    Continue,

    /// <summary>
    /// Foreground moved to a different window, or the foreground is
    /// unobservable (either handle 0): stop typing.
    /// </summary>
    Halt,
}

/// <summary>
/// Pure mid-paste decision: is the window we started typing into still the
/// foreground window? Continue is chosen ONLY when both handles are known
/// AND equal. hwnd==0 on either side halts -- FAIL-SAFE, deliberately the
/// OPPOSITE bias from probe/elevation observation failures
/// (ForegroundElevation.Unknown => inject, unchanged): GetForegroundWindow()
/// returning 0 correlates with exactly the dangerous moment. Probe evidence
/// (pending-paste-council-hardening, 2026-07-28): 0-readings occur only in
/// 0.3-3.7 ms bursts during focus transitions and never at rest, and the
/// ~0.8 s paced send window makes catching one mid-run realistic. A halt
/// parks the FULL text (visible one-click recovery); typing into an unknown
/// foreground can silently lose it. Supersedes the 2026-07-26
/// midpaste-focus-fallback fail-open pins (owner-approved). Compares raw
/// HWNDs (not UIA element identity) because this runs between every send
/// chunk and must stay cheap.
/// </summary>
public static class MidPasteDecider
{
    public static MidPasteDecision Decide(long hwndAtSendStart, long hwndNow)
    {
        if (hwndAtSendStart == 0 || hwndNow == 0) return MidPasteDecision.Halt;
        return hwndNow == hwndAtSendStart
            ? MidPasteDecision.Continue
            : MidPasteDecision.Halt;
    }
}
