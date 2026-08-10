using Shouldly;
using Winpepper.Models;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests.ViewModels;

public class ModelsTabViewModelDownloadSelectedTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("winpepper-dl-selected-").FullName;

    private sealed class RecordingDownloader : ModelsTabViewModel.IDownloader
    {
        public List<string> Downloaded { get; } = [];

        public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                  IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Downloaded.Add(descriptor.Name);
            return Task.CompletedTask;
        }
    }

    private ModelsTabViewModel CreateVm(ModelsTabViewModel.IDownloader downloader) =>
        new(new ModelRegistry(), _root, downloader,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            currentStreamingName: ModelRegistry.StreamingAsrName,
            promoteAsr: _ => { }, promoteCleanup: _ => { }, promoteStreaming: _ => { });

    [Fact]
    public async Task Downloads_Exactly_The_Given_Descriptors_In_Order()
    {
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var registry = new ModelRegistry();
        var selected = new[]
        {
            registry.Find(ModelRegistry.DefaultAsrName)!,
            registry.Find(ModelRegistry.StreamingAsrName)!,
        };

        await vm.DownloadSelectedAsync(selected, TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBe(
            new[] { ModelRegistry.DefaultAsrName, ModelRegistry.StreamingAsrName });
    }

    [Fact]
    public async Task Does_Not_Download_Unlisted_Registry_Models()
    {
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var registry = new ModelRegistry();

        await vm.DownloadSelectedAsync(
            new[] { registry.Find(ModelRegistry.DefaultCleanupName)! },
            TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBe(new[] { ModelRegistry.DefaultCleanupName });
    }

    [Fact]
    public async Task Skips_Manual_Install_Only_Descriptors()
    {
        // Belt-and-braces: the policy filters these upstream, and the raw
        // downloader would throw InvalidOperationException if one got through.
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var sotto = new ModelRegistry().Find("sotto-cleanup-lfm25-350m-q8_0")!;
        sotto.ManualInstallOnly.ShouldBeTrue(); // pin the registry assumption

        await vm.DownloadSelectedAsync(new[] { sotto }, TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Raises_IsSelectedInstalled_Changed_On_All_Three_Cards()
    {
        var vm = CreateVm(new RecordingDownloader());
        var changed = new List<string>();
        vm.AsrCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("asr"); };
        vm.CleanupCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("cleanup"); };
        vm.StreamingCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("streaming"); };

        await vm.DownloadSelectedAsync([], TestContext.Current.CancellationToken);

        changed.ShouldContain("asr");
        changed.ShouldContain("cleanup");
        changed.ShouldContain("streaming");
    }
}
