namespace Winpepper.Models;

/// <summary>
/// Pure decision logic for the Models page: which selected models are
/// missing and downloadable, whether the bottom download button should be
/// enabled, which selected models can only be installed manually, and the
/// cleanup-gate state. Pure decision — Linux-tested by design. The page
/// supplies installed-state inputs from the same sources it already
/// renders (the hash-verified flag for ASR, presence checks for cleanup
/// and streaming), so this class never touches the file system.
/// </summary>
public static class SelectedModelsPolicy
{
    /// <summary>One dropdown's current choice, reduced to what decisions need.</summary>
    public readonly record struct SelectedModel(string Name, bool IsInstalled, bool IsManualInstallOnly);

    /// <summary>
    /// The set of models the page's dropdowns currently choose. The cleanup
    /// choice only counts while cleanup is enabled (change 4's gate): a
    /// disabled feature's model is not "selected" for download purposes.
    /// Null slots (no selection in that combo) are skipped.
    /// </summary>
    public static IReadOnlyList<SelectedModel> BuildSelection(
        SelectedModel? asr, SelectedModel? streaming, SelectedModel? cleanup, bool cleanupEnabled)
    {
        var selection = new List<SelectedModel>(3);
        if (asr is { } a) selection.Add(a);
        if (streaming is { } s) selection.Add(s);
        if (cleanupEnabled && cleanup is { } c) selection.Add(c);
        return selection;
    }

    /// <summary>Selected, not installed, and fetchable by the downloader — the bottom button's work list.</summary>
    public static IReadOnlyList<string> DownloadableMissingNames(IReadOnlyList<SelectedModel> selection) =>
        selection.Where(m => !m.IsInstalled && !m.IsManualInstallOnly)
                 .Select(m => m.Name)
                 .Distinct(StringComparer.Ordinal)
                 .ToList();

    /// <summary>Selected, not installed, but manual-install only — the button must not attempt these; the UI explains instead.</summary>
    public static IReadOnlyList<string> ManualOnlyMissingNames(IReadOnlyList<SelectedModel> selection) =>
        selection.Where(m => !m.IsInstalled && m.IsManualInstallOnly)
                 .Select(m => m.Name)
                 .Distinct(StringComparer.Ordinal)
                 .ToList();

    /// <summary>A button whose only effect is already satisfied must be disabled, not hidden.</summary>
    public static bool DownloadButtonEnabled(IReadOnlyList<SelectedModel> selection) =>
        DownloadableMissingNames(selection).Count > 0;

    /// <summary>Gray out (never hide, never clear): the combo disables, values are preserved.</summary>
    public static bool CleanupCardEnabled(bool cleanupEnabled) => cleanupEnabled;

    /// <summary>The note shows exactly when the card is gated off.</summary>
    public static bool CleanupOffNoteVisible(bool cleanupEnabled) => !cleanupEnabled;
}
