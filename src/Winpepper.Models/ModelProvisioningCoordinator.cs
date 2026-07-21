namespace Winpepper.Models;

public enum ModelProvisioningStatus
{
    Missing,
    Downloading,
    Verifying,
    Retrying,
    Ready,
    Failed,
}

public sealed record ModelProvisioningState(
    ModelProvisioningStatus Status,
    DownloadProgress? Progress = null,
    string? ErrorMessage = null,
    double ProgressPercent = 0);

/// <summary>
/// Provides one authoritative, verified model-provisioning operation for all
/// callers. Caller cancellation stops waiting but does not tear down a shared
/// download that another page may still need.
/// </summary>
public sealed class ModelProvisioningCoordinator
{
    public delegate Task DownloadModel(
        ModelDescriptor descriptor,
        string installRoot,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken);

    public delegate Task<bool> VerifyModelFile(
        string path,
        ModelFile file,
        CancellationToken cancellationToken);

    private readonly object _gate = new();
    private readonly string _installRoot;
    private readonly DownloadModel _download;
    private readonly VerifyModelFile _verifyFile;
    private readonly Dictionary<string, DescriptorQueue> _queues = new(StringComparer.Ordinal);
    private ModelProvisioningState _state = new(ModelProvisioningStatus.Missing);

    public ModelProvisioningCoordinator(
        string installRoot,
        DownloadModel download,
        VerifyModelFile? verifyFile = null)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        _download = download ?? throw new ArgumentNullException(nameof(download));
        _verifyFile = verifyFile ?? VerifyFileAsync;
    }

    public ModelProvisioningState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsReady => State.Status == ModelProvisioningStatus.Ready;

    public event EventHandler<ModelProvisioningState>? StateChanged;

    public Task<bool> VerifyReadyAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        TaskCompletionSource start;
        Task<bool> operation;
        lock (_gate)
        {
            var queue = GetQueue(descriptor.Name);
            start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = VerifyReadyQueuedAsync(start.Task, queue.Tail, descriptor, ct);
            queue.Tail = operation;
        }

        start.TrySetResult();
        return operation;
    }

    private async Task<bool> VerifyReadyQueuedAsync(
        Task start, Task predecessor, ModelDescriptor descriptor, CancellationToken ct)
    {
        await start.ConfigureAwait(false);
        await IgnoreFailureAsync(predecessor).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var previousState = State;
        try
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Verifying));
            var ready = await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false);
            SetState(new ModelProvisioningState(
                ready ? ModelProvisioningStatus.Ready : ModelProvisioningStatus.Missing,
                ProgressPercent: ready ? 100 : 0));
            return ready;
        }
        catch (OperationCanceledException)
        {
            SetState(previousState.Status == ModelProvisioningStatus.Verifying
                ? new ModelProvisioningState(ModelProvisioningStatus.Missing)
                : previousState);
            throw;
        }
        catch (Exception ex)
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Failed, ErrorMessage: ex.Message));
            throw;
        }
    }

    public Task EnsureReadyAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Task operation;
        TaskCompletionSource? start = null;
        lock (_gate)
        {
            var queue = GetQueue(descriptor.Name);
            if (queue.EnsureOperation is null || queue.EnsureOperation.IsCompleted)
            {
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = EnsureReadyQueuedAsync(start.Task, queue.Tail, descriptor);
                queue.EnsureOperation = operation;
                queue.Tail = operation;
            }
            else operation = queue.EnsureOperation;
        }

        start?.TrySetResult();
        return operation.WaitAsync(ct);
    }

    public async Task RetryAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        await EnsureReadyAsync(descriptor, ct).ConfigureAwait(false);
    }

    private async Task EnsureReadyQueuedAsync(
        Task start, Task predecessor, ModelDescriptor descriptor)
    {
        await start.ConfigureAwait(false);
        await IgnoreFailureAsync(predecessor).ConfigureAwait(false);
        if (State.Status == ModelProvisioningStatus.Failed)
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Retrying));
        await EnsureReadyCoreAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EnsureReadyCoreAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Verifying));
            if (await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false))
            {
                SetState(new ModelProvisioningState(
                    ModelProvisioningStatus.Ready, ProgressPercent: 100));
                return;
            }

            SetState(new ModelProvisioningState(ModelProvisioningStatus.Missing));
            var bytesByFile = descriptor.Files.ToDictionary(
                file => file.RelativePath, _ => 0L, StringComparer.Ordinal);
            var progress = new DirectProgress<DownloadProgress>(report =>
            {
                if (bytesByFile.ContainsKey(report.FileRelativePath))
                    bytesByFile[report.FileRelativePath] = Math.Clamp(
                        report.BytesDownloaded, 0, Math.Max(0, report.TotalBytes));
                var aggregate = descriptor.TotalSizeBytes <= 0
                    ? 0
                    : 100.0 * bytesByFile.Values.Sum() / descriptor.TotalSizeBytes;
                SetState(new ModelProvisioningState(
                    ModelProvisioningStatus.Downloading,
                    report,
                    ProgressPercent: Math.Clamp(aggregate, 0, 100)));
            });
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Downloading));
            await _download(descriptor, _installRoot, progress, ct).ConfigureAwait(false);

            SetState(new ModelProvisioningState(
                ModelProvisioningStatus.Verifying, ProgressPercent: 100));
            if (!await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false))
                throw new ModelDownloadException($"Downloaded model '{descriptor.Name}' failed size or SHA-256 verification.");

            SetState(new ModelProvisioningState(
                ModelProvisioningStatus.Ready, ProgressPercent: 100));
        }
        catch (OperationCanceledException)
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Missing));
            throw;
        }
        catch (Exception ex)
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Failed, ErrorMessage: ex.Message));
            throw;
        }
    }

    private async Task<bool> VerifyFilesAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        foreach (var file in descriptor.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_installRoot, descriptor.InstallDirRelative, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes)
                return false;
            if (!await _verifyFile(path, file, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    private void SetState(ModelProvisioningState state)
    {
        lock (_gate) _state = state;
        StateChanged?.Invoke(this, state);
    }

    private DescriptorQueue GetQueue(string descriptorName)
    {
        if (!_queues.TryGetValue(descriptorName, out var queue))
        {
            queue = new DescriptorQueue();
            _queues.Add(descriptorName, queue);
        }
        return queue;
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* the queued successor must still run */ }
    }

    private static Task<bool> VerifyFileAsync(string path, ModelFile file, CancellationToken ct)
        => ChecksumVerifier.VerifyAsync(path, file.Sha256, ct);

    private sealed class DescriptorQueue
    {
        public Task Tail { get; set; } = Task.CompletedTask;
        public Task? EnsureOperation { get; set; }
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
