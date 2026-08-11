namespace Winpepper.Cleanup;

/// <summary>
/// Pure launch decision for the window-context prefetch at listen-start
/// (the new tbc0-ocr regime), extracted as its own policy home so the
/// listen-start arm of PipelineHost shares one Linux-tested decision with
/// no duplication. Sibling of <see cref="WindowContextPrefetchGate"/>;
/// differs in exactly one ruling -- the listen-start snapshot is the target
/// window and is KEPT across a mid-recording focus switch, so the launch
/// decision is made once, at listen-start, and never re-evaluated at stop.
///
/// Rulings recorded here (single source of truth for the listen-start regime):
/// <list type="bullet">
/// <item>Launch exactly ONCE per dictation, at listen-start. Never at stop:
/// the snapshot taken here is the window the user was focused on when they
/// began speaking, which is the window their words refer to even if they
/// alt-tab mid-utterance. Re-snapshotting at stop would target whatever
/// window happened to be foregrounded seconds later, silently corrupting
/// context without any signal the UI could surface.</item>
/// <item>Staleness ruling -- snapshot == target window at listen-start, kept
/// across a mid-recording focus switch. A mid-recording focus change does NOT
/// invalidate the snapshotted context; the at-stop regime's re-snapshot step
/// is dropped entirely. This is a deliberate correctness invariant, not a
/// latency optimization.</item>
/// <item>hwnd-zero telemetry note -- when no foreground window was captured
/// (hwndAtStartNonZero == false), no context task is supplied at all, and the
/// timing line emits ctx_src OMITTED rather than the at-stop regime's "none".
/// This is a deliberate diagnostic-only change: OMITTED distinguishes "policy
/// declined, no contributor" from "contributor ran but produced nothing",
/// which the "none" token could never disambiguate.</item>
/// <item>Cleanup-disabled / raw-io skips are inherited from
/// <see cref="WindowContextPrefetchGate"/> by delegation. There is exactly
/// one policy home for those rulings (the prefetch gate); this type adds
/// only the listen-start-specific precondition on top.</item>
/// </list>
/// </summary>
public static class WindowContextListenStartPolicy
{
    public static bool ShouldStart(
        bool cleanupEnabled,
        bool windowContextEnabled,
        string? activePromptFormat,
        bool hwndAtStartNonZero)
        => hwndAtStartNonZero
           && WindowContextPrefetchGate.ShouldPrefetch(cleanupEnabled, windowContextEnabled, activePromptFormat);
}
