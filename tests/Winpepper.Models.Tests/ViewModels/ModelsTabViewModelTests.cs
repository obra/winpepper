using Shouldly;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests.ViewModels;

public class ModelsTabViewModelTests : IDisposable
{
    private readonly string _root;
    public ModelsTabViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmmodels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Initialize_BuildsOneCardPerKind()
    {
        var registry = new ModelRegistry();
        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        vm.AsrCard.SelectedName.ShouldBe("parakeet-tdt-0.6b-v3");
        vm.CleanupCard.SelectedName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        vm.AsrCard.Available.ShouldAllBe(d => d.Kind == ModelKind.Asr);
        vm.CleanupCard.Available.ShouldAllBe(d => d.Kind == ModelKind.Cleanup);
    }

    [Fact]
    public void IsInstalled_ReflectsDisk()
    {
        var registry = new ModelRegistry();
        var d = registry.Find("parakeet-tdt-0.6b-v3")!;
        var modelDir = Path.Combine(_root, d.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        foreach (var f in d.Files)
            File.WriteAllText(Path.Combine(modelDir, f.RelativePath), "x");

        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: d.Name, currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        vm.AsrCard.IsSelectedInstalled.ShouldBeTrue();
        vm.CleanupCard.IsSelectedInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadMissingAsync_OnlyEnqueuesMissingSelected()
    {
        var registry = new ModelRegistry();
        var fake = new FakeDownloader();
        var vm = new ModelsTabViewModel(registry, _root, fake,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        await vm.DownloadMissingAsync(CancellationToken.None);

        fake.DownloadedNames.ShouldContain("parakeet-tdt-0.6b-v3");
        fake.DownloadedNames.ShouldContain("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void SetAsrSelection_FiresPromote()
    {
        var registry = new ModelRegistry();
        string? promoted = null;
        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: n => promoted = n, promoteCleanup: _ => { });

        vm.AsrCard.SelectedName = "parakeet-tdt-0.6b-v3";
        vm.AsrCard.CommitSelection();
        promoted.ShouldBe("parakeet-tdt-0.6b-v3");
    }
}

internal sealed class FakeDownloader : ModelsTabViewModel.IDownloader
{
    public List<string> DownloadedNames { get; } = new();

    public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                              IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        DownloadedNames.Add(descriptor.Name);
        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = descriptor.Files[0].RelativePath,
            BytesDownloaded = descriptor.Files[0].SizeBytes,
            TotalBytes = descriptor.Files[0].SizeBytes,
            Phase = DownloadPhase.Complete,
        });
        return Task.CompletedTask;
    }
}
