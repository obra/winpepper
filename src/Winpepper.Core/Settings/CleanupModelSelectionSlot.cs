namespace Winpepper.Core.Settings;

/// <summary>
/// Thread-safe in-memory source of truth for the DESIRED cleanup model name.
/// UI promote callbacks <see cref="Publish"/> the newly selected RAW name (in
/// addition to persisting it to settings.json for durability across restarts);
/// the cleanup-backend holder <see cref="Read"/>s it, both when pre-warming on
/// promote and at the pipeline's per-dictation seam. This is the cross-thread
/// transport for "effective immediately" — the settings-file round-trip is
/// NOT: on Windows an atomic replace of settings.json can fail against a
/// concurrently open read handle, silently dropping the promote.
/// A volatile reference is sufficient: single word-sized publication,
/// last-write-wins, no compound state.
/// (Mirror of <see cref="AsrModelSelectionSlot"/>.)
/// </summary>
public sealed class CleanupModelSelectionSlot
{
    private volatile string? _desired;

    /// <summary>Publish the newly selected raw model name (UI thread).</summary>
    public void Publish(string? modelName) => _desired = modelName;

    /// <summary>Read the currently desired raw model name (holder / pipeline loop).</summary>
    public string? Read() => _desired;
}
