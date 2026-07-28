namespace Winpepper.Core.Pending;

/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when the
/// paste could not be delivered (focus moved, halt gesture, elevated target,
/// no observable foreground). NEVER persisted to disk -- history archiving
/// is a separate, unchanged feature. Lifecycle:
/// None -> Pending(text,target,reason) [-> Pending(text + ' ' + more, ...)]*
/// -> consumed (successful pill-click paste) | app exit (memory-only).
/// A new dictation NEVER discards the slot and cancel preserves it (council
/// constraint, 2026-07-28: preserve/append or fail loud -- never silently
/// drop; supersedes Rule 5 of the 2026-07-21 pending-paste plan,
/// owner-approved). A dictation that parks while the slot is occupied
/// APPENDS, so one pill click pastes everything, oldest first, always the
/// COMPLETE text -- never a remainder.
/// </summary>
public sealed class PendingPasteState
{
    /// <summary>
    /// Separator between appended dictations. A space, not a newline:
    /// injected text is typed as keystrokes and Enter submits in many chat
    /// inputs -- a newline could fire a half-composed message.
    /// </summary>
    internal const string AppendSeparator = " ";

    public bool HasPending { get; private set; }
    public string PendingText { get; private set; } = string.Empty;
    public InjectionTarget Target { get; private set; } = InjectionTarget.Empty;

    /// <summary>Why the LATEST park happened -- selects the pill copy.</summary>
    public PendingPasteReason Reason { get; private set; } = PendingPasteReason.Interrupted;

    /// <summary>
    /// Hold text as pending. Empty slot: takes the text as-is. Occupied
    /// slot: APPENDS (never replaces -- no dictation is ever silently
    /// dropped). Target and Reason always track the LATEST park (freshest
    /// context for the pill copy).
    /// </summary>
    public void HoldOrAppend(string text, InjectionTarget target, PendingPasteReason reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        var incoming = text ?? string.Empty;
        if (HasPending && PendingText.Length > 0 && incoming.Length > 0)
            PendingText = PendingText + AppendSeparator + incoming;
        else if (!HasPending || incoming.Length > 0)
            PendingText = incoming;
        // else: occupied slot + empty incoming -- keep the held text.
        Target = target;
        Reason = reason;
        HasPending = true;
    }

    /// <summary>Clear the slot (successful paste, or app exit). Idempotent.</summary>
    public void Discard()
    {
        HasPending = false;
        PendingText = string.Empty;
        Target = InjectionTarget.Empty;
        Reason = PendingPasteReason.Interrupted;
    }

    /// <summary>
    /// Apply the outcome of a pill-click paste attempt. On success the slot is
    /// consumed (cleared). On failure the slot is KEPT so the user can click
    /// again. Returns true when the slot was consumed.
    /// </summary>
    public bool OnPasteAttempted(bool injected)
    {
        if (!HasPending) return false;
        if (injected) { Discard(); return true; }
        return false;
    }
}
