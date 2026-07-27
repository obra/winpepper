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
    public void IsSelectedDownloadable_FalseForManualInstallOnlySelection_TrueOtherwise()
    {
        var registry = new ModelRegistry();
        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        vm.CleanupCard.IsSelectedDownloadable.ShouldBeTrue();

        vm.CleanupCard.SelectedName = "sotto-cleanup-lfm25-350m-q8_0";
        vm.CleanupCard.IsSelectedDownloadable.ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadMissingAsync_ManualInstallOnlySelection_IsSkippedGracefully()
    {
        var registry = new ModelRegistry();
        var fake = new FakeDownloader();
        var vm = new ModelsTabViewModel(registry, _root, fake,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "sotto-cleanup-lfm25-350m-q8_0",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        await vm.DownloadMissingAsync(CancellationToken.None);

        // ASR still routes through provisioning; the manual-only cleanup model
        // never reaches the downloader (nothing to download).
        fake.DownloadedNames.ShouldBe(["parakeet-tdt-0.6b-v3"]);
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
    public async Task DownloadMissingAsync_AlwaysRoutesSelectedAsrThroughAuthoritativeProvisioning()
    {
        var registry = new ModelRegistry();
        foreach (var descriptor in registry.All)
        {
            var modelDir = Path.Combine(_root, descriptor.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            foreach (var file in descriptor.Files)
                await File.WriteAllTextAsync(
                    Path.Combine(modelDir, file.RelativePath), "nonempty but unverified",
                    TestContext.Current.CancellationToken);
        }
        var fake = new FakeDownloader();
        var vm = new ModelsTabViewModel(registry, _root, fake,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        await vm.DownloadMissingAsync(TestContext.Current.CancellationToken);

        fake.DownloadedNames.ShouldBe([ModelRegistry.DefaultAsrName]);
    }

    [Fact]
    public async Task DownloadMissingAsync_DoesNotCaptureAmbientUiContextForEitherDescriptor()
    {
        var dispatcher = new ManualDispatcher();
        var context = new CountingSynchronizationContext();
        var vm = new ModelsTabViewModel(new ModelRegistry(), _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { },
            dispatch: dispatcher.Post,
            progressInterval: TimeSpan.Zero);

        var previous = SynchronizationContext.Current;
        Task download;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            download = vm.DownloadMissingAsync(CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await dispatcher.RunUntilAsync(() => download.IsCompleted);
        await download;

        context.PostCount.ShouldBe(0,
            "the progress bridge must not inherit Progress<T>'s ambient UI-context hop");
        // The bridge itself is single-flight. Completion then posts one
        // installed-state notification per card, so the whole tab's constant
        // upper bound is two rather than scaling with download chunks.
        dispatcher.MaxPendingCount.ShouldBeLessThanOrEqualTo(2);
        vm.AsrCard.ProgressByFile.Single().Phase.ShouldBe(DownloadPhase.Complete);
        vm.CleanupCard.ProgressByFile.Single().Phase.ShouldBe(DownloadPhase.Complete);
    }

    [Fact]
    public async Task DownloadMissingAsync_ShowsIntermediateBurstProgressWithoutGrowingUiQueue()
    {
        var dispatcher = new ManualDispatcher();
        var downloader = new GatedBurstDownloader();
        var vm = new ModelsTabViewModel(new ModelRegistry(), _root, downloader,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { },
            dispatch: dispatcher.Post,
            progressInterval: TimeSpan.Zero);
        var asrPhases = new List<DownloadPhase>();
        vm.AsrCard.ProgressByFile.CollectionChanged += (_, e) =>
        {
            if (e.NewItems?[0] is DownloadProgress progress) asrPhases.Add(progress.Phase);
        };

        var download = vm.DownloadMissingAsync(CancellationToken.None);
        await downloader.BurstReported;

        dispatcher.MaxPendingCount.ShouldBe(1);
        await dispatcher.RunUntilAsync(() =>
            vm.AsrCard.ProgressByFile.Any(progress =>
                progress.Phase == DownloadPhase.Downloading && progress.BytesDownloaded > 0));

        download.IsCompleted.ShouldBeFalse();
        var intermediate = vm.AsrCard.ProgressByFile.Single();
        intermediate.PercentComplete.ShouldBeGreaterThan(0.0);
        intermediate.PercentComplete.ShouldBeLessThan(100.0);
        dispatcher.MaxPendingCount.ShouldBe(1);

        downloader.Release();
        await dispatcher.RunUntilAsync(() => download.IsCompleted);
        await download;

        vm.AsrCard.ProgressByFile.ShouldAllBe(progress => progress.Phase == DownloadPhase.Complete);
        vm.AsrCard.ProgressByFile.ShouldAllBe(progress => progress.PercentComplete == 100.0);
        asrPhases.ShouldContain(DownloadPhase.Verifying);
        asrPhases[^1].ShouldBe(DownloadPhase.Complete);
        vm.CleanupCard.ProgressByFile.ShouldAllBe(progress => progress.Phase == DownloadPhase.Complete);
        dispatcher.MaxPendingCount.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task DownloadMissingAsync_SerializesViewModelsSharingDownloader()
    {
        var downloader = new GatedBurstDownloader();
        var firstVm = new ModelsTabViewModel(new ModelRegistry(), _root, downloader,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });
        var secondVm = new ModelsTabViewModel(new ModelRegistry(), _root, downloader,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        var first = firstVm.DownloadMissingAsync(CancellationToken.None);
        await downloader.BurstReported;
        var second = secondVm.DownloadMissingAsync(CancellationToken.None);

        downloader.DownloadCount.ShouldBe(1);
        second.IsCompleted.ShouldBeFalse(
            "a newly navigated Models page must share the active service operation gate");

        downloader.Release();
        await Task.WhenAll(first, second);

        downloader.DownloadCount.ShouldBe(4,
            "each request may re-check both models, but downloader calls must never overlap");
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

internal sealed class CountingSynchronizationContext : SynchronizationContext
{
    private int _postCount;
    public int PostCount => Volatile.Read(ref _postCount);
    public override void Post(SendOrPostCallback d, object? state)
        => Interlocked.Increment(ref _postCount);
}

internal sealed class ManualDispatcher
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queued = new();

    public int MaxPendingCount { get; private set; }

    public void Post(Action action)
    {
        lock (_gate)
        {
            _queued.Enqueue(action);
            MaxPendingCount = Math.Max(MaxPendingCount, _queued.Count);
        }
    }

    public async Task RunUntilAsync(Func<bool> done)
    {
        for (var attempt = 0; attempt < 100_000; attempt++)
        {
            if (done()) return;

            Action? action = null;
            lock (_gate)
            {
                if (_queued.Count > 0) action = _queued.Dequeue();
            }

            if (action is not null) action();
            else await Task.Yield();
        }

        throw new TimeoutException("The model download did not drain through the manual dispatcher.");
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

internal sealed class GatedBurstDownloader : ModelsTabViewModel.IDownloader
{
    private readonly TaskCompletionSource<bool> _burstReported =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _downloadCount;

    public Task BurstReported => _burstReported.Task;
    public int DownloadCount => Volatile.Read(ref _downloadCount);
    public void Release() => _release.TrySetResult(true);

    public async Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                    IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var file = descriptor.Files[0];
        if (Interlocked.Increment(ref _downloadCount) == 1)
        {
            progress.Report(Report(descriptor, file, 0, DownloadPhase.Downloading));
            var halfway = file.SizeBytes / 2;
            for (var i = 1; i <= 10_000; i++)
                progress.Report(Report(descriptor, file, halfway * i / 10_000, DownloadPhase.Downloading));

            _burstReported.TrySetResult(true);
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            progress.Report(Report(descriptor, file, file.SizeBytes, DownloadPhase.Verifying));
        }

        progress.Report(Report(descriptor, file, file.SizeBytes, DownloadPhase.Complete));
    }

    private static DownloadProgress Report(ModelDescriptor descriptor, ModelFile file,
                                           long bytes, DownloadPhase phase) => new()
    {
        DescriptorName = descriptor.Name,
        FileRelativePath = file.RelativePath,
        BytesDownloaded = bytes,
        TotalBytes = file.SizeBytes,
        Phase = phase,
    };
}
