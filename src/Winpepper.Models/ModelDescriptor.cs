namespace Winpepper.Models;

/// <summary>
/// A model the user can pick. The registry (<see cref="ModelRegistry"/>) holds
/// the canonical list. The downloader (<see cref="ModelDownloader"/>) iterates
/// <see cref="Files"/> to fetch missing pieces.
/// </summary>
public sealed record ModelDescriptor
{
    /// <summary>Stable id used in <c>AppSettings.AsrModelName</c> / <c>CleanupModelName</c>.</summary>
    public required string Name { get; init; }

    public required ModelKind Kind { get; init; }

    /// <summary>Human-readable label for the Models tab.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Path under <c>%LOCALAPPDATA%\winpepper\models\</c> where files land.
    /// For Parakeet, this is <c>parakeet-tdt-0.6b-v3</c>.
    /// For cleanup models, this is <c>cleanup\&lt;name&gt;</c>.
    /// </summary>
    public required string InstallDirRelative { get; init; }

    public required IReadOnlyList<ModelFile> Files { get; init; }

    /// <summary>
    /// Cleanup-LLM prompt format id (see <c>Winpepper.Cleanup.CleanupPromptFormatter</c>:
    /// "chatml", "granite", "raw-io"). Only meaningful for
    /// <see cref="ModelKind.Cleanup"/> descriptors; defaults to chatml, the
    /// format of the registry-default qwen model.
    /// </summary>
    public string PromptFormat { get; init; } = "chatml";

    /// <summary>
    /// True for cleanup models that pattern-complete the worked example
    /// embedded in the default base prompt instead of cleaning the transcript
    /// (LFM2.5-1.2B returned the example output verbatim for unrelated
    /// dictations). When set, <c>CleanupRunner</c> uses the example-free
    /// default prompt (<c>BasePrompts.DefaultNoExample</c>). The registry
    /// default qwen model NEEDS the example (bug-3 fix-(iv)), so this
    /// defaults to false.
    /// </summary>
    public bool OmitPromptExample { get; init; }

    /// <summary>
    /// True when the model has no downloadable source (e.g. a locally
    /// converted GGUF): the downloader refuses it and
    /// <see cref="MissingModelsResolver"/> never selects it, but once the
    /// user places the files under <see cref="InstallDirRelative"/>,
    /// <see cref="IsFullyInstalled"/>, path resolution, eval and bench treat
    /// it like any other model.
    /// </summary>
    public bool ManualInstallOnly { get; init; }

    /// <summary>Sum of file sizes in bytes.</summary>
    public long TotalSizeBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>True when every file in the descriptor exists, is non-empty, on disk.</summary>
    public bool IsFullyInstalled(string installRoot)
    {
        foreach (var f in Files)
        {
            var p = Path.Combine(installRoot, InstallDirRelative, f.RelativePath);
            if (!File.Exists(p)) return false;
            if (new FileInfo(p).Length == 0) return false;
        }
        return true;
    }
}
