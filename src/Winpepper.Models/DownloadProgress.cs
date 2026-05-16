namespace Winpepper.Models;

public enum DownloadPhase
{
    Pending = 0,
    Downloading = 1,
    Verifying = 2,
    Complete = 3,
    Failed = 4,
}

/// <summary>
/// Reported via <see cref="IProgress{T}"/> as a download advances. UI binds
/// the latest <see cref="DownloadProgress"/> per (descriptor, file) pair.
/// </summary>
public sealed record DownloadProgress
{
    public required string DescriptorName { get; init; }
    public required string FileRelativePath { get; init; }
    public required long BytesDownloaded { get; init; }
    public required long TotalBytes { get; init; }
    public required DownloadPhase Phase { get; init; }
    public string? ErrorMessage { get; init; }

    public double PercentComplete => TotalBytes <= 0 ? 0.0 : 100.0 * BytesDownloaded / TotalBytes;
}
