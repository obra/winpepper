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
        _downloadGate = DownloadGates.GetValue(
            downloader, static _ => new SemaphoreSlim(1, 1));

        AsrCard = new ModelCardViewModel(ModelKind.Asr,
            registry.ByKind(ModelKind.Asr), installRoot, currentAsrName, promoteAsr,
            dispatch, progressInterval, progressDelay);
        CleanupCard = new ModelCardViewModel(ModelKind.Cleanup,
            registry.ByKind(ModelKind.Cleanup), installRoot, currentCleanupName, promoteCleanup,
            dispatch, progressInterval, progressDelay);
    }

    public ModelCardViewModel AsrCard { get; }
    public ModelCardViewModel CleanupCard { get; }

    public async Task DownloadMissingAsync(CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var resolver = new MissingModelsResolver();
            var selectedNames = new[] { AsrCard.SelectedName, CleanupCard.SelectedName };
            var missing = resolver.FindMissing(_registry.All, _installRoot, selectedNames);

            foreach (var d in missing)
            {
                var card = d.Kind == ModelKind.Asr ? AsrCard : CleanupCard;
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

            AsrCard.RaiseIsSelectedInstalledChanged();
            CleanupCard.RaiseIsSelectedInstalledChanged();
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
