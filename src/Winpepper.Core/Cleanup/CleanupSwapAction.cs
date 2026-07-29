namespace Winpepper.Core.Cleanup;

/// <summary>
/// The decision produced by <see cref="CleanupModelSwapState.Plan"/> about what
/// the cleanup-backend holder should do with its live backend+runner pair at
/// the next dictation's cleanup seam.
/// </summary>
public enum CleanupSwapAction
{
    /// <summary>Keep the currently loaded backend; no swap needed.</summary>
    KeepCurrent,

    /// <summary>Nothing is loaded yet; adopt the desired model's pre-warmed pair.</summary>
    Load,

    /// <summary>A different model is desired and its pre-warmed pair is ready; swap to it.</summary>
    Swap,

    /// <summary>
    /// Nothing loaded and the desired model is not ready. Unlike the ASR analog
    /// this is NON-FATAL: the dictation proceeds with the raw transcript (no
    /// cleanup) and a later dictation re-evaluates once a pre-warm completes.
    /// </summary>
    CannotStart,
}
