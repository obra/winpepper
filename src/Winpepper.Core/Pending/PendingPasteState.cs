namespace Winpepper.Core.Pending;

/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when the
/// paste could not be delivered (focus moved, halt gesture, elevated target,
/// no observable foreground). NEVER persisted to disk -- history archiving
/// is a separate, unchanged feature. Lifecycle:
/// None -> Pending(text,target,reason)
/// -> consumed (successful pill-click paste)
/// | DISMISSED when the user starts a NEW dictation (owner directive
///   2026-08-12: "dismiss the click to paste as soon as a new recording
///   starts" -- starting to talk again declares the deferred text abandoned;
///   supersedes the council 2026-07-28 preserve/append policy)
/// | app exit (memory-only).
/// HoldOrAppend KEEPS its never-replace append semantics for an occupied
/// slot as a defensive component guarantee, although production paths now
/// always park into an empty slot (the Recording-arm discard runs first).
/// Cancel preserves the slot: a cancel happens mid-dictation, and any park
/// then alive belongs to that same dictation.
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

    /// <summary>Clear the slot (successful paste, new-dictation dismissal, or app exit). Idempotent.</summary>
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
