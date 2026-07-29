#if WINDOWS
using Winpepper.Models;
using Winpepper.Models.ViewModels;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Services;

public sealed class ModelsServices : ModelsTabViewModel.IDownloader, IAsrProvisioningService, IDisposable
{
    private AsrProvisioningState _state = new(AsrProvisioningStatus.Missing);

    public ModelsServices(string modelsRoot, string? asrModelName = null)
    {
        ModelsRoot = modelsRoot;
        Registry = new ModelRegistry();
        AsrDescriptor = Registry.ResolveOrDefault(asrModelName, ModelKind.Asr);
        _http = new HttpClientRangeClient();
        _downloader = new ModelDownloader(_http);
        _coordinator = new ModelProvisioningCoordinator(modelsRoot, _downloader.DownloadAsync);
        _coordinator.StateChanged += OnCoordinatorStateChanged;
        _state = MapState(_coordinator.State);
    }

    public string ModelsRoot { get; }
    public ModelRegistry Registry { get; }
    public ModelDescriptor AsrDescriptor { get; }
    public AsrProvisioningState State => _state;

    public event EventHandler<AsrProvisioningState>? StateChanged;

    private readonly HttpClientRangeClient _http;
    private readonly ModelDownloader _downloader;
    private readonly ModelProvisioningCoordinator _coordinator;

    public async Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                    IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (descriptor.Kind != ModelKind.Asr)
        {
            await _downloader.DownloadAsync(descriptor, installRoot, progress, ct);
            return;
        }

        void ForwardProgress(object? _, ModelProvisioningState state)
        {
            if (state.Progress is not null) progress.Report(state.Progress);
        }

        _coordinator.StateChanged += ForwardProgress;
        try
        {
            await _coordinator.EnsureReadyAsync(descriptor, ct);
        }
        finally
        {
            _coordinator.StateChanged -= ForwardProgress;
        }
    }

    public Task EnsureReadyAsync(CancellationToken ct)
        => _coordinator.EnsureReadyAsync(AsrDescriptor, ct);

    public async Task<bool> VerifyReadyAsync(CancellationToken ct)
    {
        var ready = await _coordinator.VerifyReadyAsync(AsrDescriptor, ct);
        // Seed the synchronous cache: the boot model just passed the same
        // descriptor-level size+SHA-256 off the UI thread, so a UI-thread
        // TryStart() reading AsrDescriptor.Name below need not re-hash.
        if (ready) _verifiedAsrModelName = AsrDescriptor.Name;
        return ready;
    }

    private string? _verifiedAsrModelName; // last canonical name that passed descriptor-level verification

    /// <summary>
    /// Descriptor-level verified readiness (per-file size + SHA-256 via
    /// ModelProvisioningCoordinator.VerifyReadyAsync, which queues behind any
    /// in-flight download) for the CANONICAL model name — resolved per-name
    /// because <see cref="AsrDescriptor"/> is frozen at boot. The positive
    /// result is CACHED per selection change: a full ~1.1 GB SHA-256 on every
    /// dictation start is too slow, so we re-verify only when the requested
    /// name differs from the last verified one. A negative result is never
    /// cached (missing files short-circuit cheaply, and the next dictation
    /// should pick up a completed download). Only the per-descriptor
    /// VerifyReadyAsync return is authoritative — the coordinator's global
    /// <see cref="State"/> is not a per-model signal.
    /// </summary>
    public bool VerifyAsrModelReady(string canonicalName)
    {
        if (string.Equals(_verifiedAsrModelName, canonicalName, StringComparison.Ordinal))
            return true;

        var descriptor = Registry.ResolveOrDefault(canonicalName, ModelKind.Asr);
        var ready = _coordinator.VerifyReadyAsync(descriptor, CancellationToken.None)
                                .GetAwaiter().GetResult();
        if (ready) _verifiedAsrModelName = canonicalName;
        return ready;
    }

    private string? _verifiedCleanupModelName; // last canonical cleanup name that passed descriptor-level verification

    /// <summary>
    /// Cleanup analog of <see cref="VerifyAsrModelReady"/>: descriptor-level
    /// verified readiness (per-file size + SHA-256) for the CANONICAL cleanup
    /// model name. The positive result is cached per selection change; a
    /// negative result is never cached (missing files short-circuit cheaply,
    /// and the next attempt should pick up a completed download). Deliberately
    /// does NOT route through ModelProvisioningCoordinator.VerifyReadyAsync:
    /// that would churn the coordinator's single global state, which feeds the
    /// ASR startup gate, onboarding, and the Models page. Called only from the
    /// cleanup pre-warm background thread — never from the UI thread or the
    /// dictation seam — so a cold multi-second SHA-256 here is safe.
    /// </summary>
    public bool VerifyCleanupModelReady(string canonicalName)
    {
        if (string.Equals(_verifiedCleanupModelName, canonicalName, StringComparison.Ordinal))
            return true;

        var descriptor = Registry.ResolveOrDefault(canonicalName, ModelKind.Cleanup);
        var ready = ModelFilesVerifier.VerifyAsync(descriptor, ModelsRoot, CancellationToken.None)
                                      .GetAwaiter().GetResult();
        if (ready) _verifiedCleanupModelName = canonicalName;
        return ready;
    }

    private void OnCoordinatorStateChanged(object? sender, ModelProvisioningState state)
    {
        _state = MapState(state);
        StateChanged?.Invoke(this, _state);
    }

    private static AsrProvisioningState MapState(ModelProvisioningState state) => new(
        state.Status switch
        {
            ModelProvisioningStatus.Missing => AsrProvisioningStatus.Missing,
            ModelProvisioningStatus.Downloading => AsrProvisioningStatus.Downloading,
            ModelProvisioningStatus.Verifying => AsrProvisioningStatus.Verifying,
            ModelProvisioningStatus.Retrying => AsrProvisioningStatus.Retrying,
            ModelProvisioningStatus.Ready => AsrProvisioningStatus.Ready,
            ModelProvisioningStatus.Failed => AsrProvisioningStatus.Failed,
            _ => AsrProvisioningStatus.Failed,
        },
        state.ProgressPercent,
        state.ErrorMessage);

    public void Dispose()
    {
        _coordinator.StateChanged -= OnCoordinatorStateChanged;
        _http.Dispose();
    }
}
#endif
