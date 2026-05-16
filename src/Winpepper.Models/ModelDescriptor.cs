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
