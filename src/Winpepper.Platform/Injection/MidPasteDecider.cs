namespace Winpepper.Platform.Injection;

/// <summary>Per-chunk continue/halt outcome while a paste is in flight.</summary>
public enum MidPasteDecision
{
    /// <summary>Same window still foreground, or identity unknown: keep typing.</summary>
    Continue,

    /// <summary>Foreground positively moved to a DIFFERENT window: stop typing.</summary>
    Halt,
}

/// <summary>
/// Pure mid-paste decision: is the window we started typing into still the
/// foreground window? Halt is chosen ONLY when we positively know the
/// foreground changed (both handles known and different). If either handle is
/// unknown (0) we default to Continue — same fail-open bias as
/// <c>PendingPasteDecider</c>: we never regress into holding when we simply
/// failed to observe. Compares raw HWNDs (not UIA element identity) because
/// this runs between every send chunk and must stay cheap.
/// </summary>
public static class MidPasteDecider
{
    public static MidPasteDecision Decide(long hwndAtSendStart, long hwndNow)
    {
        if (hwndAtSendStart == 0 || hwndNow == 0) return MidPasteDecision.Continue;
        return hwndNow == hwndAtSendStart
            ? MidPasteDecision.Continue
            : MidPasteDecision.Halt;
    }
}
