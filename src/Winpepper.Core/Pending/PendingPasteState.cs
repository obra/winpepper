namespace Winpepper.Core.Pending;

/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when focus
/// moved away from the original target before injection. This slot is NEVER
/// persisted to disk — history archiving is a separate, unchanged feature.
/// Lifecycle: None -> Pending(text,target) -> consumed (successful paste) |
/// discarded (next hotkey / cancel / app exit — app exit is memory-only, so
/// trivially discarded).
/// </summary>
public sealed class PendingPasteState
{
    public bool HasPending { get; private set; }
    public string PendingText { get; private set; } = string.Empty;
    public InjectionTarget Target { get; private set; } = InjectionTarget.Empty;

    /// <summary>Hold text as pending, replacing any existing pending slot.</summary>
    public void SetPending(string text, InjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        PendingText = text ?? string.Empty;
        Target = target;
        HasPending = true;
    }

    /// <summary>Clear the slot (next hotkey, cancel, or app exit). Idempotent.</summary>
    public void Discard()
    {
        HasPending = false;
        PendingText = string.Empty;
        Target = InjectionTarget.Empty;
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
