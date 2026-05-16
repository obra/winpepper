namespace Winpepper.Models;

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Downloads every file in a <see cref="ModelDescriptor"/> to its install
/// directory. Resumes from <c>.partial</c> temp files; verifies SHA-256
/// before renaming into place; reports progress per chunk and per phase.
/// </summary>
public sealed class ModelDownloader
{
    private readonly IHttpRangeClient _http;

    public ModelDownloader(IHttpRangeClient http)
    {
        _http = http;
    }

    public async Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                    IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var modelDir = Path.Combine(installRoot, descriptor.InstallDirRelative);
        Directory.CreateDirectory(modelDir);

        foreach (var file in descriptor.Files)
        {
            await DownloadOneAsync(descriptor, modelDir, file, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadOneAsync(ModelDescriptor descriptor, string modelDir, ModelFile file,
                                        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var finalPath = Path.Combine(modelDir, file.RelativePath);
        var partialPath = finalPath + ".partial";

        // 1) If the final file already exists and verifies, skip.
        if (File.Exists(finalPath))
        {
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = new FileInfo(finalPath).Length,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Verifying,
            });
            if (await ChecksumVerifier.VerifyAsync(finalPath, file.Sha256, ct).ConfigureAwait(false))
            {
                progress.Report(new DownloadProgress
                {
                    DescriptorName = descriptor.Name,
                    FileRelativePath = file.RelativePath,
                    BytesDownloaded = new FileInfo(finalPath).Length,
                    TotalBytes = file.SizeBytes,
                    Phase = DownloadPhase.Complete,
                });
                return;
            }
            // Stale/corrupt — start over.
            File.Delete(finalPath);
        }

        // 2) Determine resume offset.
        long startByte = 0;
        if (File.Exists(partialPath))
        {
            startByte = new FileInfo(partialPath).Length;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        }

        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = startByte,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Downloading,
        });

        // 3) Stream bytes.
        var totalBytes = startByte;
        try
        {
            await using (var fs = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None,
                                                 bufferSize: 64 * 1024, useAsync: true))
            {
                await foreach (var chunk in _http.GetRangeAsync(file.Url, startByte, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    await fs.WriteAsync(chunk, ct).ConfigureAwait(false);
                    totalBytes += chunk.Length;
                    progress.Report(new DownloadProgress
                    {
                        DescriptorName = descriptor.Name,
                        FileRelativePath = file.RelativePath,
                        BytesDownloaded = totalBytes,
                        TotalBytes = file.SizeBytes,
                        Phase = DownloadPhase.Downloading,
                    });
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = totalBytes,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Failed,
                ErrorMessage = ex.Message,
            });
            throw new ModelDownloadException($"Download of {file.Url} failed: {ex.Message}", ex);
        }

        // 4) Verify checksum on the partial.
        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = totalBytes,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Verifying,
        });

        var ok = await ChecksumVerifier.VerifyAsync(partialPath, file.Sha256, ct).ConfigureAwait(false);
        if (!ok)
        {
            TryDelete(partialPath);
            TryDelete(finalPath);
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = totalBytes,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Failed,
                ErrorMessage = "SHA-256 mismatch",
            });
            throw new ModelDownloadException($"SHA-256 mismatch on {file.RelativePath}");
        }

        // 5) Promote .partial to final.
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partialPath, finalPath);

        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = totalBytes,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Complete,
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
