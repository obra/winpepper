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
/// Two DISTINCT failure policies (council, 2026-07-28):
/// - Probe/elevation unobservable (hwnd known, ForegroundElevation.Unknown):
///   INJECT -- unchanged fail-open; a transient probe failure must not
///   regress the common path. Same bias as PendingPasteDecider.
/// - Foreground hwnd ABSENT (0): PARK -- fail-safe; there is no window to
///   verify anything against and hwnd==0 correlates with focus transitions
///   (probe evidence, 2026-07-28). Normally unreachable: TextInjector
///   returns NoForeground before consulting this decider; this arm is
///   defense in depth for any other caller.
/// </summary>
public static class ElevatedTargetDecider
{
    public static ElevatedTargetDecision Decide(long hwndAtSendStart, ForegroundElevation elevation)
    {
        if (hwndAtSendStart == 0) return ElevatedTargetDecision.Park; // foreground ABSENT: fail safe (see class doc)
        return elevation == ForegroundElevation.Elevated
            ? ElevatedTargetDecision.Park
            : ElevatedTargetDecision.Inject;
    }
}
