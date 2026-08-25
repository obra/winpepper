namespace Winpepper.Core.Asr;

/// <summary>
/// Pure decision state for live ASR model swapping. Holds which local model is
/// currently loaded and decides, per dictation, whether to keep it, load the
/// first session, or swap to a newly selected model.
///
/// Caller contract: model names passed to <see cref="Plan"/> and
/// <see cref="CommitLoad"/> are RESOLVED canonical descriptor names (the host
/// resolves the raw settings value via ModelRegistry.ResolveOrDefault first),
/// and the readiness flag is descriptor-level verified provisioning, not bare
/// file existence.
///
/// State only advances via <see cref="CommitLoad"/>, which the host calls after
/// a session is successfully (re)loaded. If a load fails, the host does not call
/// CommitLoad, so <see cref="LoadedModelName"/> keeps naming the previous
/// working model — this is the "keep-old-on-failure" guarantee expressed in
/// pure, testable code.
/// </summary>
public sealed class AsrModelSwapState
{
    /// <summary>The model whose session is currently loaded; null until first load.</summary>
    public string? LoadedModelName { get; private set; }

    /// <summary>Number of successful (re)loads so far; starts at 0.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Decide what to do for the next dictation given the desired model (from
    /// settings) and whether its files are present/verified on disk. Pure: does
    /// not mutate state.
    /// </summary>
    public AsrSwapAction Plan(string desiredModelName, bool desiredFilesPresent)
    {
        if (LoadedModelName is null)
            return desiredFilesPresent ? AsrSwapAction.Load : AsrSwapAction.CannotStart;

        if (string.Equals(desiredModelName, LoadedModelName, StringComparison.Ordinal))
            return AsrSwapAction.KeepCurrent;

        // A different model is selected. Swap only if its files are present;
        // otherwise keep the current working session until the download/verify
        // completes (a later dictation will re-evaluate and swap).
        return desiredFilesPresent ? AsrSwapAction.Swap : AsrSwapAction.KeepCurrent;
    }

    /// <summary>
    /// Record that a session for <paramref name="modelName"/> was successfully
    /// loaded. Advances state: sets the loaded name and increments the generation.
    /// </summary>
    public void CommitLoad(string modelName)
    {
        LoadedModelName = modelName;
        Generation++;
    }

    /// <summary>
    /// Record that no backup session is held (PipelineHost disposes the session
    /// itself when the desired selection becomes "None"). Without this, a later
    /// re-selection of the SAME name would Plan <c>KeepCurrent</c> against a
    /// session that no longer exists (2026-08-25). Generation is left intact —
    /// it counts loads, not occupancy.
    /// </summary>
    public void MarkUnloaded() => LoadedModelName = null;
}
