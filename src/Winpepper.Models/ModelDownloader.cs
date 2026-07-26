namespace Winpepper.Models;

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ModelDownloaderOptions
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay hook used for the fixed one- and two-second retry backoffs.</summary>
    public Func<TimeSpan, CancellationToken, Task> RetryDelayAsync { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);
}

/// <summary>
/// Downloads every file in a <see cref="ModelDescriptor"/> to its install
/// directory. Resumes from <c>.partial</c> temp files; verifies size and
/// SHA-256 before atomically renaming into place; reports progress per chunk
/// and per phase.
/// </summary>
public sealed class ModelDownloader
{
    private const int CopyBufferSize = 64 * 1024;
    private const int MaxAttempts = 3;

    private readonly IHttpRangeClient _http;
    private readonly ModelDownloaderOptions _options;

    public ModelDownloader(IHttpRangeClient http) : this(http, new ModelDownloaderOptions()) { }

    public ModelDownloader(IHttpRangeClient http, ModelDownloaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Idle timeout must be positive.");
        }
        ArgumentNullException.ThrowIfNull(options.RetryDelayAsync);

        _http = http;
        _options = options;
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

        if (File.Exists(finalPath))
        {
            var finalSize = new FileInfo(finalPath).Length;
            Report(progress, descriptor, file, finalSize, DownloadPhase.Verifying);
            if (finalSize == file.SizeBytes &&
                await ChecksumVerifier.VerifyAsync(finalPath, file.Sha256, ct).ConfigureAwait(false))
            {
                EnsureExtracted(modelDir, file);
                Report(progress, descriptor, file, finalSize, DownloadPhase.Complete);
                return;
            }

            File.Delete(finalPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        if (File.Exists(partialPath) && new FileInfo(partialPath).Length > file.SizeBytes)
        {
            TryDelete(partialPath);
        }

        var totalBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        Report(progress, descriptor, file, totalBytes, DownloadPhase.Downloading);

        Exception? downloadError = null;
        if (totalBytes < file.SizeBytes)
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var requestedStartByte = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                try
                {
                    totalBytes = await DownloadAttemptAsync(
                        descriptor, file, partialPath, requestedStartByte, progress, ct).ConfigureAwait(false);

                    if (totalBytes < file.SizeBytes)
                    {
                        throw new IOException(
                            $"Download ended after {totalBytes} of {file.SizeBytes} bytes.");
                    }

                    downloadError = null;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var failure = ex is OperationCanceledException
                        ? new TimeoutException(
                            $"Download made no progress for {_options.IdleTimeout.TotalSeconds:0.###} seconds.", ex)
                        : ex;

                    if (failure is InvalidDownloadSizeException)
                    {
                        TryDelete(partialPath);
                    }

                    totalBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                    var isTransient = IsTransient(failure);
                    if (isTransient && totalBytes == file.SizeBytes)
                    {
                        downloadError = null;
                        break;
                    }
                    if (isTransient && attempt < MaxAttempts)
                    {
                        downloadError = failure;
                        await _options.RetryDelayAsync(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                        continue;
                    }

                    downloadError = failure;
                    break;
                }
            }
        }

        if (downloadError is not null)
        {
            Report(progress, descriptor, file, totalBytes, DownloadPhase.Failed, downloadError.Message);
            if (downloadError is ModelDownloadException modelDownloadException)
            {
                throw modelDownloadException;
            }
            throw new ModelDownloadException(
                $"Download of {file.Url} failed: {downloadError.Message}", downloadError);
        }

        Report(progress, descriptor, file, totalBytes, DownloadPhase.Verifying);

        var actualSize = new FileInfo(partialPath).Length;
        var sizeMatches = actualSize == file.SizeBytes;
        var hashMatches = sizeMatches &&
            await ChecksumVerifier.VerifyAsync(partialPath, file.Sha256, ct).ConfigureAwait(false);
        if (!sizeMatches || !hashMatches)
        {
            TryDelete(partialPath);
            TryDelete(finalPath);
            var errorMessage = sizeMatches
                ? "SHA-256 mismatch"
                : $"Size mismatch (expected {file.SizeBytes} bytes, received {actualSize})";
            Report(progress, descriptor, file, totalBytes, DownloadPhase.Failed, errorMessage);
            throw new ModelDownloadException($"{errorMessage} on {file.RelativePath}");
        }

        File.Move(partialPath, finalPath, overwrite: true);
        EnsureExtracted(modelDir, file);
        Report(progress, descriptor, file, actualSize, DownloadPhase.Complete);
    }

    private async Task<long> DownloadAttemptAsync(
        ModelDescriptor descriptor,
        ModelFile file,
        string partialPath,
        long requestedStartByte,
        IProgress<DownloadProgress> progress,
        CancellationToken ct)
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(_options.IdleTimeout);
        var response = await _http.GetRangeAsync(file.Url, requestedStartByte, idleCts.Token)
            .ConfigureAwait(false);
        await using (response.ConfigureAwait(false))
        {
            if (response.ContentStartByte != requestedStartByte &&
                !(requestedStartByte > 0 && response.ContentStartByte == 0))
            {
                throw new ModelDownloadException(
                    $"Server returned content starting at byte {response.ContentStartByte} for byte range {requestedStartByte}-.");
            }

            var fileMode = response.ContentStartByte == 0 ? FileMode.Create : FileMode.Append;
            var totalBytes = response.ContentStartByte;
            await using var fs = new FileStream(
                partialPath, fileMode, FileAccess.Write, FileShare.None,
                bufferSize: CopyBufferSize, useAsync: true);
            var buffer = new byte[CopyBufferSize];

            while (true)
            {
                var bytesRead = await response.Content.ReadAsync(buffer.AsMemory(), idleCts.Token)
                    .ConfigureAwait(false);

                if (bytesRead == 0)
                {
                    break;
                }
                idleCts.CancelAfter(_options.IdleTimeout);
                if (totalBytes + bytesRead > file.SizeBytes)
                {
                    throw new InvalidDownloadSizeException(
                        $"Download exceeded the declared size of {file.SizeBytes} bytes.");
                }

                await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalBytes += bytesRead;
                Report(progress, descriptor, file, totalBytes, DownloadPhase.Downloading);
            }

            await fs.FlushAsync(ct).ConfigureAwait(false);
            return totalBytes;
        }
    }

    private static bool IsTransient(Exception ex)
    {
        if (ex is IOException or TimeoutException)
        {
            return true;
        }
        if (ex is not HttpRequestException httpRequestException)
        {
            return false;
        }

        var status = httpRequestException.StatusCode;
        return status is null ||
               status == System.Net.HttpStatusCode.RequestTimeout ||
               status == System.Net.HttpStatusCode.TooManyRequests ||
               (int)status >= 500;
    }

    private static void Report(
        IProgress<DownloadProgress> progress,
        ModelDescriptor descriptor,
        ModelFile file,
        long bytesDownloaded,
        DownloadPhase phase,
        string? errorMessage = null) =>
        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = file.SizeBytes,
            Phase = phase,
            ErrorMessage = errorMessage,
        });

    private static void EnsureExtracted(string modelDir, ModelFile file)
    {
        if (file.ExtractToRelative is null) return;
        TarGzExtractor.EnsureExtracted(
            Path.Combine(modelDir, file.RelativePath),
            Path.Combine(modelDir, file.ExtractToRelative),
            file.Sha256);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }

    private sealed class InvalidDownloadSizeException(string message) : Exception(message);
}
