using Winpepper.Models.Tests.ViewModels;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelsTabViewModelStreamingTests : IDisposable
{
    private readonly string _root;

    public ModelsTabViewModelStreamingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmstreaming-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private ModelsTabViewModel CreateVm(ModelsTabViewModel.IDownloader downloader) =>
        new(new ModelRegistry(), _root, downloader,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            promoteAsr: _ => { }, promoteCleanup: _ => { });

    [Fact]
    public void StreamingCard_lists_exactly_the_nemotron_descriptor()
    {
        var vm = CreateVm(new FakeDownloader());
        var names = vm.StreamingCard.Available.Select(d => d.Name).ToList();
        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, names);
    }

    [Fact]
    public void Streaming_descriptor_is_not_in_the_batch_asr_card()
    {
        var vm = CreateVm(new FakeDownloader());
        Assert.DoesNotContain(vm.AsrCard.Available, d => d.Kind == ModelKind.StreamingAsr);
    }

    [Fact]
    public async Task DownloadStreamingAsync_downloads_exactly_the_nemotron_descriptor_when_missing()
    {
        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadStreamingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task DownloadStreamingAsync_skips_download_when_fully_installed()
    {
        var registry = new ModelRegistry();
        var d = registry.Find(ModelRegistry.StreamingAsrName)!;
        var modelDir = Path.Combine(_root, d.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        foreach (var f in d.Files)
        {
            var path = Path.Combine(modelDir, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);
        }
        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadStreamingAsync(TestContext.Current.CancellationToken);

        Assert.Empty(fake.DownloadedNames);
    }

    [Fact]
    public async Task DownloadStreamingAsync_routes_progress_to_the_streaming_card()
    {
        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadStreamingAsync(TestContext.Current.CancellationToken);

        var progress = Assert.Single(vm.StreamingCard.ProgressByFile);
        Assert.Equal(ModelRegistry.StreamingAsrName, progress.DescriptorName);
        Assert.Empty(vm.AsrCard.ProgressByFile);
        Assert.Empty(vm.CleanupCard.ProgressByFile);
    }
}
