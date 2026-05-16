#if WINDOWS
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Services;

public sealed class ModelsServices : ModelsTabViewModel.IDownloader, IDisposable
{
    public ModelsServices(string modelsRoot)
    {
        ModelsRoot = modelsRoot;
        Registry = new ModelRegistry();
        _http = new HttpClientRangeClient();
        _downloader = new ModelDownloader(_http);
    }

    public string ModelsRoot { get; }
    public ModelRegistry Registry { get; }

    private readonly HttpClientRangeClient _http;
    private readonly ModelDownloader _downloader;

    public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                              IProgress<DownloadProgress> progress, CancellationToken ct)
        => _downloader.DownloadAsync(descriptor, installRoot, progress, ct);

    public void Dispose() => _http.Dispose();
}
#endif
