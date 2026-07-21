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
    string? ErrorMessage = null);

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

    private readonly object _gate = new();
    private readonly string _installRoot;
    private readonly DownloadModel _download;
    private readonly Dictionary<string, Task> _operations = new(StringComparer.Ordinal);
    private ModelProvisioningState _state = new(ModelProvisioningStatus.Missing);

    public ModelProvisioningCoordinator(string installRoot, DownloadModel download)
    {
        _installRoot = installRoot ?? throw new ArgumentNullException(nameof(installRoot));
        _download = download ?? throw new ArgumentNullException(nameof(download));
    }

    public ModelProvisioningState State
    {
        get { lock (_gate) return _state; }
    }

    public bool IsReady => State.Status == ModelProvisioningStatus.Ready;

    public event EventHandler<ModelProvisioningState>? StateChanged;

    public async Task<bool> VerifyReadyAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Task? activeOperation;
        lock (_gate)
        {
            _operations.TryGetValue(descriptor.Name, out activeOperation);
            if (activeOperation?.IsCompleted == true) activeOperation = null;
        }

        if (activeOperation is not null)
        {
            try
            {
                await activeOperation.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // The verification below remains authoritative even when the
                // active provisioning operation failed.
            }
        }

        try
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Verifying));
            var ready = await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false);
            SetState(new ModelProvisioningState(
                ready ? ModelProvisioningStatus.Ready : ModelProvisioningStatus.Missing));
            return ready;
        }
        catch (OperationCanceledException)
        {
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
        ModelProvisioningState? retryState = null;
        lock (_gate)
        {
            if (!_operations.TryGetValue(descriptor.Name, out operation!) || operation.IsCompleted)
            {
                if (_state.Status == ModelProvisioningStatus.Failed)
                {
                    retryState = new ModelProvisioningState(ModelProvisioningStatus.Retrying);
                    _state = retryState;
                }
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                operation = EnsureReadyAfterStartAsync(start.Task, descriptor);
                _operations[descriptor.Name] = operation;
            }
        }

        if (retryState is not null)
        {
            try { StateChanged?.Invoke(this, retryState); }
            finally { start!.TrySetResult(); }
        }
        else
        {
            start?.TrySetResult();
        }

        return operation.WaitAsync(ct);
    }

    public async Task RetryAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        SetState(new ModelProvisioningState(ModelProvisioningStatus.Retrying));
        await EnsureReadyAsync(descriptor, ct).ConfigureAwait(false);
    }

    private async Task EnsureReadyAfterStartAsync(Task start, ModelDescriptor descriptor)
    {
        await start.ConfigureAwait(false);
        await EnsureReadyCoreAsync(descriptor, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EnsureReadyCoreAsync(ModelDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Verifying));
            if (await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false))
            {
                SetState(new ModelProvisioningState(ModelProvisioningStatus.Ready));
                return;
            }

            SetState(new ModelProvisioningState(ModelProvisioningStatus.Missing));
            var progress = new DirectProgress<DownloadProgress>(report =>
                SetState(new ModelProvisioningState(ModelProvisioningStatus.Downloading, report)));
            SetState(new ModelProvisioningState(ModelProvisioningStatus.Downloading));
            await _download(descriptor, _installRoot, progress, ct).ConfigureAwait(false);

            SetState(new ModelProvisioningState(ModelProvisioningStatus.Verifying));
            if (!await VerifyFilesAsync(descriptor, ct).ConfigureAwait(false))
                throw new ModelDownloadException($"Downloaded model '{descriptor.Name}' failed size or SHA-256 verification.");

            SetState(new ModelProvisioningState(ModelProvisioningStatus.Ready));
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
            if (!await ChecksumVerifier.VerifyAsync(path, file.Sha256, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }

    private void SetState(ModelProvisioningState state)
    {
        lock (_gate) _state = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
