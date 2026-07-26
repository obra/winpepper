using Winpepper.Models.ViewModels;

namespace Winpepper.Models;

public enum StreamingAutoInstallStatus
{
    NotStarted,
    SkippedStreamingDisabled,
    Installing,
    Installed,
    Failed,
}

/// <summary>
/// First-run auto-install of the Nemotron streaming model + native runtime.
/// The batch ASR model gets its install-on-first-run treatment through the
/// blocking onboarding download step; the streaming model gets the same
/// outcome without blocking anything: the host fires <see cref="StartAsync"/>
/// on every launch, dictation works immediately via the batch path, and
/// streaming activates on the first dictation after the install lands
/// (PipelineHost re-checks installed state per dictation).
///
/// Policies, mirrored from the batch model where they apply:
/// - Retry: transient network retry lives in <see cref="ModelDownloader"/>;
///   beyond that there is no background retry loop — a failed attempt is
///   simply re-run on the next launch or from the Models card.
/// - Failure: never throws. A failed install leaves the app fully functional
///   on batch transcription; the outcome is observable via <see cref="Status"/>
///   / <see cref="LastError"/> for logging and the Models card.
/// - Single flight: one in-flight operation per installer, and the download
///   itself serializes on <see cref="ModelsTabViewModel.SharedOperationGateFor"/>
///   so a Models-card install during an auto-install can never write the same
///   files concurrently (the later run's verify short-circuit keeps it cheap).
/// - Repair: a healthy install short-circuits on cheap checks (exact file
///   sizes + extraction marker) because this runs every launch; a broken
///   extraction fails that check and routes through the downloader, whose
///   EnsureExtracted heal path (commit 1672ae6) repairs it. Deep SHA-256
///   repair stays on the Models card, which always routes through the
///   downloader.
/// </summary>
public sealed class StreamingAutoInstaller
{
    private readonly ModelDescriptor _descriptor;
    private readonly string _installRoot;
    private readonly ModelsTabViewModel.IDownloader _downloader;
    private readonly SemaphoreSlim _operationGate;
    private readonly object _gate = new();
    private Task? _current;
    private StreamingAutoInstallStatus _status = StreamingAutoInstallStatus.NotStarted;

    public StreamingAutoInstaller(
        ModelRegistry registry, string installRoot, ModelsTabViewModel.IDownloader downloader)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(installRoot);
        ArgumentNullException.ThrowIfNull(downloader);
        _descriptor = registry.Find(ModelRegistry.StreamingAsrName)
            ?? throw new InvalidOperationException(
                $"Streaming model '{ModelRegistry.StreamingAsrName}' is absent from the registry.");
        _installRoot = installRoot;
        _downloader = downloader;
        _operationGate = ModelsTabViewModel.SharedOperationGateFor(downloader);
    }

    public StreamingAutoInstallStatus Status
    {
        get { lock (_gate) return _status; }
    }

    /// <summary>Message of the last failed attempt; null while none failed.</summary>
    public string? LastError { get; private set; }

    public event EventHandler<StreamingAutoInstallStatus>? StatusChanged;

    /// <summary>
    /// Begin (or join) the auto-install. Never throws: the returned task
    /// completes when the attempt ends, with the outcome in <see cref="Status"/>.
    /// Concurrent calls share the one in-flight operation; a call after a
    /// finished attempt re-evaluates (retry after failure, cheap no-op once
    /// installed).
    /// </summary>
    public Task StartAsync(bool streamingEnabled, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_current is { IsCompleted: false }) return _current;
            if (_status == StreamingAutoInstallStatus.Installed) return Task.CompletedTask;
            _current = RunAsync(streamingEnabled, ct);
            return _current;
        }
    }

    private async Task RunAsync(bool streamingEnabled, CancellationToken ct)
    {
        try
        {
            if (!streamingEnabled)
            {
                SetStatus(StreamingAutoInstallStatus.SkippedStreamingDisabled);
                return;
            }

            if (IsInstalledAndExtracted())
            {
                SetStatus(StreamingAutoInstallStatus.Installed);
                return;
            }

            SetStatus(StreamingAutoInstallStatus.Installing);
            await _operationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _downloader.DownloadAsync(_descriptor, _installRoot, NullProgress.Instance, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }

            LastError = null;
            SetStatus(StreamingAutoInstallStatus.Installed);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            SetStatus(StreamingAutoInstallStatus.Failed);
        }
    }

    /// <summary>Cheap per-launch health check: every file at its exact declared
    /// size, and every archive carrying a completed-extraction marker for its
    /// pinned SHA-256 plus the extracted tree.</summary>
    private bool IsInstalledAndExtracted()
    {
        var modelDir = Path.Combine(_installRoot, _descriptor.InstallDirRelative);
        foreach (var f in _descriptor.Files)
        {
            var path = Path.Combine(modelDir, f.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != f.SizeBytes) return false;
            if (f.ExtractToRelative is { } extractTo &&
                !TarGzExtractor.IsExtracted(path, Path.Combine(modelDir, extractTo), f.Sha256))
            {
                return false;
            }
        }
        return true;
    }

    private void SetStatus(StreamingAutoInstallStatus status)
    {
        lock (_gate) _status = status;
        // Contain subscriber exceptions: StartAsync's never-throw contract
        // must hold mechanically, not depend on every subscriber behaving.
        // Status is already committed above, so observers via Status still
        // see the update.
        try { StatusChanged?.Invoke(this, status); }
        catch { /* subscriber fault must not fault the install */ }
    }

    private sealed class NullProgress : IProgress<DownloadProgress>
    {
        public static readonly NullProgress Instance = new();
        public void Report(DownloadProgress value) { }
    }
}
