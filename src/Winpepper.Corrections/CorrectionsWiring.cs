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
    public static CorrectionsViewModel CreateViewModel(CorrectionStore store)
    {
        var initial = store.Load();
        return new CorrectionsViewModel(
            initial.Preferred,
            initial.Replacements,
            (preferred, replacements) => store.Save(new CorrectionsData
            {
                Preferred = preferred,
                Replacements = replacements,
            }));
    }
}
