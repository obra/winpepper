namespace Winpepper.Core.Cleanup;

/// <summary>
/// Pure decision state for live cleanup-model swapping (mirror of
/// <c>Winpepper.Core.Asr.AsrModelSwapState</c>). Holds which model's
/// backend+runner pair is currently live and decides, per dictation, whether
/// to keep it, adopt a first pair, or swap to a newly selected model.
///
/// Caller contract: model names passed to <see cref="Plan"/> and
/// <see cref="CommitLoad"/> are RESOLVED canonical descriptor names (the host
/// resolves the raw settings value via ModelRegistry.ResolveOrDefault first),
/// and the readiness flag means a hash-verified (per-file size + SHA-256),
/// fully pre-warmed backend for the desired model is ready to adopt — not
/// bare file existence, and never an in-flight load.
///
/// State only advances via <see cref="CommitLoad"/>, which the holder calls
/// after a pair is successfully adopted. If a load/pre-warm fails, the holder
/// does not call CommitLoad, so <see cref="LoadedModelName"/> keeps naming the
/// previous working model — the "keep-old-on-failure" guarantee in pure,
/// testable code. <see cref="LoadedModelName"/> is also the value history
/// records stamp: it names the model that actually ran.
/// </summary>
public sealed class CleanupModelSwapState
{
    /// <summary>The model whose pair is currently live; null until first load.</summary>
    public string? LoadedModelName { get; private set; }

    /// <summary>Number of successful (re)loads so far; starts at 0.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Decide what to do at the next dictation's cleanup seam given the desired
    /// model and whether a verified pre-warmed pair for it is ready. Pure: does
    /// not mutate state.
    /// </summary>
    public CleanupSwapAction Plan(string desiredModelName, bool desiredReady)
    {
        if (LoadedModelName is null)
            return desiredReady ? CleanupSwapAction.Load : CleanupSwapAction.CannotStart;

        if (string.Equals(desiredModelName, LoadedModelName, StringComparison.Ordinal))
            return CleanupSwapAction.KeepCurrent;

        // A different model is selected. Swap only if its pre-warmed pair is
        // ready; otherwise keep the current working pair until the background
        // load/verification completes (a later dictation will re-evaluate).
        return desiredReady ? CleanupSwapAction.Swap : CleanupSwapAction.KeepCurrent;
    }

    /// <summary>
    /// Record that a pair for <paramref name="modelName"/> was successfully
    /// adopted. Advances state: sets the loaded name and increments the generation.
    /// </summary>
    public void CommitLoad(string modelName)
    {
        LoadedModelName = modelName;
        Generation++;
    }
}
