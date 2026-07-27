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
}
