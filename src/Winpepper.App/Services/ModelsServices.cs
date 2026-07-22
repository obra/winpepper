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

    public Task<bool> VerifyReadyAsync(CancellationToken ct)
        => _coordinator.VerifyReadyAsync(AsrDescriptor, ct);

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
