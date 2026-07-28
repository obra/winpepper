namespace Winpepper.Platform.Injection;

/// <summary>
/// Outcome of a guarded (chunked, focus-checked) injection run.
/// Pure managed; no Win32 dependency.
/// </summary>
public enum InjectionRunOutcome
{
    /// <summary>Every chunk was sent; equivalent to the old TryInject == true.</summary>
    Completed,

    /// <summary>
    /// The foreground window changed mid-paste; remaining chunks were NOT sent.
    /// The caller must fall back to a pending paste holding the WHOLE original
    /// text (never just the remainder).
    /// </summary>
    Interrupted,

    /// <summary>SendInput refused a chunk; equivalent to the old TryInject == false.</summary>
    SendFailed,

    /// <summary>
    /// The foreground window at send start belongs to an elevated
    /// (higher-integrity) process: Windows UIPI would silently drop every
    /// SendInput keystroke while reporting success, so NOTHING was typed --
    /// not even the modifier-neutralizing KEYUPs. The caller must park the
    /// FULL text as a pending paste and surface the elevated-target pill
    /// status. Not an error (no ErrorBus) -- the pill is the surface.
    /// </summary>
    BlockedElevated,

    /// <summary>
    /// GetForegroundWindow() returned 0 at send start: the foreground was
    /// unobservable at exactly the moment we were about to type. NOTHING was
    /// typed -- not even the modifier-neutralizing KEYUPs. FAIL-SAFE, the
    /// deliberate opposite of the probe/elevation fail-open bias
    /// (ForegroundElevation.Unknown => inject): probe evidence
    /// (pending-paste-council-hardening, 2026-07-28) shows hwnd==0 occurs
    /// ONLY during focus transitions -- exactly the dangerous moment -- and a
    /// park is a visible one-click detour while a blind inject can be
    /// invisible, unrecoverable loss. The caller must park the FULL text as
    /// a pending paste with the default "Click to paste" copy. Not an error
    /// (no ErrorBus) -- the pill is the surface.
    /// </summary>
    NoForeground,
}
