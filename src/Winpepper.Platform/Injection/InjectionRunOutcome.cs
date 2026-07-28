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
}
