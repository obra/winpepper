namespace Winpepper.Models;

/// <summary>
/// One downloadable file inside a model bundle.
/// </summary>
public sealed record ModelFile
{
    /// <summary>Path relative to the model's install directory, e.g. <c>encoder-model.int8.onnx</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Direct download URL (HuggingFace <c>/resolve/main/...</c>).</summary>
    public required string Url { get; init; }

    /// <summary>Lowercase hex SHA-256 of the fully downloaded file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Expected total size in bytes (used for progress + sanity).</summary>
    public required long SizeBytes { get; init; }

    /// <summary>
    /// Optional: after download + SHA-256 verification, extract this .tar.gz
    /// archive into this directory (relative to the model install dir). The
    /// archive file itself is KEPT so IsFullyInstalled and hash re-verification
    /// keep working. Extraction is idempotent (TarGzExtractor marker file).
    /// </summary>
    public string? ExtractToRelative { get; init; }
}
