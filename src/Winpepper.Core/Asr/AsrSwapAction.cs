namespace Winpepper.Core.Asr;

/// <summary>
/// The decision produced by <see cref="AsrModelSwapState.Plan"/> about what the
/// pipeline host should do with its local ASR session before the next dictation.
/// </summary>
public enum AsrSwapAction
{
    /// <summary>Keep the currently loaded session; no reload needed.</summary>
    KeepCurrent,

    /// <summary>No session is loaded yet; load the desired model.</summary>
    Load,

    /// <summary>A different model is desired and present; swap to it.</summary>
    Swap,

    /// <summary>No session loaded and the desired model's files are absent.</summary>
    CannotStart,
}
