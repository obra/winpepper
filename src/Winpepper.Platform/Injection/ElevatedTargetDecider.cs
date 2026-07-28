namespace Winpepper.Platform.Injection;

/// <summary>Foreground-window elevation as observed at injection start.</summary>
public enum ForegroundElevation
{
    /// <summary>Positively determined NOT elevated: safe to inject.</summary>
    NotElevated,

    /// <summary>
    /// Positively elevated, or access to the process/token was DENIED.
    /// Denial is read conservatively as elevated: probe evidence
    /// (paste-path-hardening, 2026-07-27) shows normal user apps are always
    /// queryable from medium IL, while protected/elevated processes deny
    /// OpenProcess -- and parking never loses text, whereas injecting into a
    /// UIPI-protected window silently loses all of it.
    /// </summary>
    Elevated,

    /// <summary>
    /// Could not observe (non-Windows, window gone before/while probing, or
    /// an unexpected probe failure): transient observation failure, handled
    /// fail-open like every other guard probe.
    /// </summary>
    Unknown,
}

/// <summary>Pre-injection decision for an elevated foreground target.</summary>
public enum ElevatedTargetDecision
{
    /// <summary>Proceed with the injection run.</summary>
    Inject,

    /// <summary>Do not inject; park the FULL text as a pending paste.</summary>
    Park,
}

/// <summary>
/// Pure pre-injection decision: is the window we are about to type into an
/// elevated (higher-integrity) process? Windows UIPI silently drops SendInput
/// to elevated windows while reporting success (MSDN: "neither GetLastError
/// nor the return value will indicate the failure was caused by UIPI
/// blocking"), so injecting would consume the text with nothing delivered.
/// Park is chosen ONLY when the foreground is positively observable
/// (hwnd != 0) AND its elevation is Elevated. An unknown HWND or unknown
/// elevation keeps today's fail-open behavior: inject. Same bias as
/// MidPasteDecider / PendingPasteDecider: never regress into holding when we
/// simply failed to observe.
/// </summary>
public static class ElevatedTargetDecider
{
    public static ElevatedTargetDecision Decide(long hwndAtSendStart, ForegroundElevation elevation)
    {
        if (hwndAtSendStart == 0) return ElevatedTargetDecision.Inject; // foreground unobservable: fail open
        return elevation == ForegroundElevation.Elevated
            ? ElevatedTargetDecision.Park
            : ElevatedTargetDecision.Inject;
    }
}
