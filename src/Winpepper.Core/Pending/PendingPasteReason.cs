namespace Winpepper.Core.Pending;

/// <summary>
/// Why text is sitting in the pending-paste slot -- selects the pill's
/// status copy. Slot semantics are identical for every reason: the FULL
/// transcription is held in memory (never persisted) and a pill click
/// re-attempts the paste into whatever field is focused then.
/// </summary>
public enum PendingPasteReason
{
    /// <summary>
    /// Deferred or interrupted paste (focus moved, halt gesture, SendInput
    /// refusal): the default "Click to paste" copy.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The target window was elevated -- UIPI would have silently dropped
    /// every keystroke (paste-path-hardening) -- so nothing was typed. The
    /// copy tells the user to focus a normal window before clicking.
    /// </summary>
    ElevatedTarget,
}
