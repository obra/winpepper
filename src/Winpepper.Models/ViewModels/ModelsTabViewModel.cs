using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Models.ViewModels;

public sealed class ModelsTabViewModel : INotifyPropertyChanged
{
    private static readonly ConditionalWeakTable<IDownloader, SemaphoreSlim> DownloadGates = new();

    public interface IDownloader
    {
        Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                           IProgress<DownloadProgress> progress, CancellationToken ct);
    }

    private readonly ModelRegistry _registry;
    private readonly string _installRoot;
    private readonly IDownloader _downloader;
    private readonly SemaphoreSlim _downloadGate;

    public ModelsTabViewModel(ModelRegistry registry, string installRoot, IDownloader downloader,
                              string currentAsrName, string currentCleanupName,
                              Action<string> promoteAsr, Action<string> promoteCleanup,
                              Action<Action>? dispatch = null,
                              TimeSpan? progressInterval = null,
                              Func<TimeSpan, Task>? progressDelay = null)
    {
        _registry = registry;
        _installRoot = installRoot;
        _downloader = downloader;
        // Models pages are recreated during navigation, while the underlying
        // downloader service is shared. Key the operation gate by that service
        // so two page view models cannot write the same model files at once.
        _downloadGate = SharedOperationGateFor(downloader);

        AsrCard = new ModelCardViewModel(ModelKind.Asr,
            registry.ByKind(ModelKind.Asr), installRoot, currentAsrName, promoteAsr,
            dispatch, progressInterval, progressDelay);
        CleanupCard = new ModelCardViewModel(ModelKind.Cleanup,
            registry.ByKind(ModelKind.Cleanup), installRoot, currentCleanupName, promoteCleanup,
            dispatch, progressInterval, progressDelay);
        // The streaming model is a single fixed descriptor: there is no
        // selection to promote, so the card pins the one name and the promote
        // callback is a no-op.
        StreamingCard = new ModelCardViewModel(ModelKind.StreamingAsr,
            registry.ByKind(ModelKind.StreamingAsr), installRoot, ModelRegistry.StreamingAsrName, _ => { },
            dispatch, progressInterval, progressDelay);
    }

    /// <summary>
    /// The per-downloader-service operation gate. Everything that writes model
    /// files through the same downloader must serialize on this one semaphore:
    /// Models page view models (recreated per navigation) and the background
    /// <see cref="StreamingAutoInstaller"/> all share it, so an install started
    /// in one place can never write the same files concurrently with another.
    /// </summary>
    public static SemaphoreSlim SharedOperationGateFor(IDownloader downloader)
        => DownloadGates.GetValue(downloader, static _ => new SemaphoreSlim(1, 1));

    public ModelCardViewModel AsrCard { get; }
    public ModelCardViewModel CleanupCard { get; }
    public ModelCardViewModel StreamingCard { get; }

    /// <summary>
    /// Downloads exactly the given descriptors — the page computes the
    /// "selected and missing" set via SelectedModelsPolicy, so this method
    /// never reaches for unselected registry models. Manual-install-only
    /// descriptors are skipped defensively: the policy filters them
    /// upstream, and the raw downloader throws if one reaches it.
    /// </summary>
    public async Task DownloadSelectedAsync(IReadOnlyList<ModelDescriptor> models, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var d in models)
            {
                if (d.ManualInstallOnly) continue;
                await DownloadOneAsync(d, ct).ConfigureAwait(false);
            }

            AsrCard.RaiseIsSelectedInstalledChanged();
            CleanupCard.RaiseIsSelectedInstalledChanged();
            StreamingCard.RaiseIsSelectedInstalledChanged();
        }
        finally { _downloadGate.Release(); }
    }

    private async Task DownloadOneAsync(ModelDescriptor d, CancellationToken ct)
    {
        var card = d.Kind switch
        {
            ModelKind.Asr => AsrCard,
            ModelKind.Cleanup => CleanupCard,
            ModelKind.StreamingAsr => StreamingCard,
            _ => throw new ArgumentOutOfRangeException(nameof(d.Kind), d.Kind, null),
        };
        var progress = new DirectProgress<DownloadProgress>(card.ReportProgress);
        try
        {
            await _downloader.DownloadAsync(d, _installRoot, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // Direct progress callbacks have all returned when the
                // downloader completes. Await the bounded UI bridge so
                // terminal state is visible before the next model.
                await card.DrainProgressAsync().ConfigureAwait(false);
            }
            finally
            {
                card.ResetProgressAfterRun();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
