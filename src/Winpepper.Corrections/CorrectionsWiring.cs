using Winpepper.Core.ViewModels;

namespace Winpepper.Corrections;

/// <summary>
/// Builds the <see cref="CorrectionsViewModel"/> for the Corrections settings
/// page: seeds it from <see cref="CorrectionStore.Load"/> and wires its
/// persist callback to <see cref="CorrectionStore.Save"/>.
///
/// Lives in Winpepper.Corrections because it is the only shared project that
/// can see both the store and the VM (Corrections -> Core; Core has no
/// project references; Winpepper.App is WinUI-bound and untestable on Linux).
/// The store path stays injected by the caller — AppPaths is App-layer.
///
/// Known interaction (deliberate, unchanged): the VM's persist is a
/// whole-document last-writer-wins Save, while the post-paste learning path
/// (<see cref="CorrectionStoreWriter"/>) does read-modify-write Add*. A
/// background-learned entry added after the VM was seeded is overwritten by
/// the next explicit UI edit.
/// </summary>
public static class CorrectionsWiring
{
    public static CorrectionsViewModel CreateViewModel(
        CorrectionStore store,
        Action<Exception>? onError = null)
    {
        CorrectionsData initial;
        var seedFailed = false;
        try
        {
            initial = store.Load();
        }
        catch (Exception ex)
        {
            // Load() swallows only JsonException; I/O errors (locked file,
            // permissions) escape it and must not crash app boot. Degrade to
            // an empty seed — the UI stays usable in-memory.
            initial = CorrectionsData.Empty;
            seedFailed = true;
            onError?.Invoke(ex);
        }

        return new CorrectionsViewModel(
            initial.Preferred,
            initial.Replacements,
            (preferred, replacements) =>
            {
                if (seedFailed)
                {
                    // The disk file may still hold healthy data this VM never
                    // saw (transient boot failure): a whole-document Save
                    // would replace it with this near-empty view — a
                    // user-data wipe. Precedent
                    // (docs/plans/2026-07-26-settings-lost-update.md): a
                    // degraded load can never become the base of a full-file
                    // rewrite. Keep the edit in memory; report every attempt.
                    onError?.Invoke(new InvalidOperationException(
                        "Corrections were not loaded at startup; refusing to overwrite corrections.json with a partial view. Restart the app to re-enable persistence."));
                    return;
                }

                try
                {
                    store.Save(new CorrectionsData
                    {
                        Preferred = preferred,
                        Replacements = replacements,
                    });
                }
                catch (Exception ex)
                {
                    // Save() deliberately rethrows I/O failures (AtomicFile),
                    // and CorrectionsViewModel.Persist() has no containment —
                    // an escape would reach the WinUI click handler. Contain
                    // here: the in-memory edit is kept; disk stays stale
                    // until the next successful persist.
                    onError?.Invoke(ex);
                }
            });
    }
}
