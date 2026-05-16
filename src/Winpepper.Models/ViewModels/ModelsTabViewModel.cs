using System.ComponentModel;

namespace Winpepper.Models.ViewModels;

public sealed class ModelsTabViewModel : INotifyPropertyChanged
{
    public interface IDownloader
    {
        Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                           IProgress<DownloadProgress> progress, CancellationToken ct);
    }

    private readonly ModelRegistry _registry;
    private readonly string _installRoot;
    private readonly IDownloader _downloader;

    public ModelsTabViewModel(ModelRegistry registry, string installRoot, IDownloader downloader,
                              string currentAsrName, string currentCleanupName,
                              Action<string> promoteAsr, Action<string> promoteCleanup)
    {
        _registry = registry;
        _installRoot = installRoot;
        _downloader = downloader;

        AsrCard = new ModelCardViewModel(ModelKind.Asr,
            registry.ByKind(ModelKind.Asr), installRoot, currentAsrName, promoteAsr);
        CleanupCard = new ModelCardViewModel(ModelKind.Cleanup,
            registry.ByKind(ModelKind.Cleanup), installRoot, currentCleanupName, promoteCleanup);
    }

    public ModelCardViewModel AsrCard { get; }
    public ModelCardViewModel CleanupCard { get; }

    public async Task DownloadMissingAsync(CancellationToken ct)
    {
        var resolver = new MissingModelsResolver();
        var selectedNames = new[] { AsrCard.SelectedName, CleanupCard.SelectedName };
        var missing = resolver.FindMissing(_registry.All, _installRoot, selectedNames);

        foreach (var d in missing)
        {
            var card = d.Kind == ModelKind.Asr ? AsrCard : CleanupCard;
            var progress = new Progress<DownloadProgress>(p => card.ReportProgress(p));
            await _downloader.DownloadAsync(d, _installRoot, progress, ct).ConfigureAwait(false);
        }

        AsrCard.RaiseIsSelectedInstalledChanged();
        CleanupCard.RaiseIsSelectedInstalledChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
