using Shouldly;
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

    private ModelsTabViewModel CreateVm(
        ModelsTabViewModel.IDownloader downloader,
        string? currentStreamingName = null,
        Action<string>? promoteStreaming = null) =>
        new(new ModelRegistry(), _root, downloader,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            currentStreamingName: currentStreamingName ?? ModelRegistry.StreamingAsrName,
            promoteAsr: _ => { }, 
            promoteCleanup: _ => { },
            promoteStreaming: promoteStreaming ?? (_ => { }));

    [Fact]
    public void StreamingCard_ListsBothNemotronModels_AndSelectsTheCurrentOne()
    {
        var vm = CreateVm(new FakeDownloader(), currentStreamingName: ModelRegistry.MultilingualStreamingAsrName);
        vm.StreamingCard.Available.Select(d => d.Name).ShouldBe(
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.MultilingualStreamingAsrName });
        vm.StreamingCard.SelectedName.ShouldBe(ModelRegistry.MultilingualStreamingAsrName);
    }

    [Fact]
    public void StreamingCard_CommitSelection_InvokesThePromoteCallback()
    {
        string? promoted = null;
        var vm = CreateVm(new FakeDownloader(), promoteStreaming: n => promoted = n);
        vm.StreamingCard.SelectedName = ModelRegistry.MultilingualStreamingAsrName;
        vm.StreamingCard.CommitSelection();
        promoted.ShouldBe(ModelRegistry.MultilingualStreamingAsrName);
    }

    [Fact]
    public void StreamingCard_lists_exactly_the_nemotron_descriptors()
    {
        var vm = CreateVm(new FakeDownloader());
        var names = vm.StreamingCard.Available.Select(d => d.Name).ToList();
        Assert.Equal(
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.MultilingualStreamingAsrName },
            names);
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

        await vm.DownloadSelectedAsync(
            new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task DownloadStreamingAsync_routes_through_downloader_even_when_fully_installed()
    {
        // A healthy install must still reach the downloader: presence-only
        // checks cannot vouch for file integrity, and the downloader's verify
        // short-circuit makes the fully-installed run cheap and idempotent.
        var registry = new ModelRegistry();
        var d = registry.Find(ModelRegistry.StreamingAsrName)!;
        var modelDir = Path.Combine(_root, d.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        foreach (var f in d.Files)
        {
            var path = Path.Combine(modelDir, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);
            if (f.ExtractToRelative is { } extractTo)
            {
                // Simulate a completed extraction: marker plus runtime tree.
                await File.WriteAllTextAsync(path + ".extracted", f.Sha256, TestContext.Current.CancellationToken);
                Directory.CreateDirectory(Path.Combine(modelDir, extractTo));
            }
        }
        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadSelectedAsync(
            new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task DownloadStreamingAsync_reaches_downloader_when_archive_present_but_extraction_missing()
    {
        // The stuck state this guards against: the archive downloaded fine but
        // extraction failed (AV lock, disk full, kill mid-extract) or the
        // extracted runtime/ tree was deleted later. IsFullyInstalled only
        // checks the archive files, so a pre-filter on it would skip the
        // downloader and make ModelDownloader's heal path (EnsureExtracted,
        // proven by Already_installed_archive_with_missing_extraction_is_healed)
        // unreachable from the UI. The install button must always route the
        // descriptor through the downloader so that heal can run.
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
        // Archive files present and nonempty, yet no extraction marker or
        // runtime tree exists anywhere: the archive-only state looks installed.
        Assert.True(d.IsFullyInstalled(_root));
        var archive = d.Files.Single(f => f.ExtractToRelative is not null);
        Assert.False(File.Exists(Path.Combine(modelDir, archive.RelativePath + ".extracted")));
        Assert.False(Directory.Exists(Path.Combine(modelDir, archive.ExtractToRelative!)));

        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadSelectedAsync(
            new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task DownloadStreamingAsync_routes_progress_to_the_streaming_card()
    {
        var fake = new FakeDownloader();
        var vm = CreateVm(fake);

        await vm.DownloadSelectedAsync(
            new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! }, TestContext.Current.CancellationToken);

        var progress = Assert.Single(vm.StreamingCard.ProgressByFile);
        Assert.Equal(ModelRegistry.StreamingAsrName, progress.DescriptorName);
        Assert.Empty(vm.AsrCard.ProgressByFile);
        Assert.Empty(vm.CleanupCard.ProgressByFile);
    }
}
