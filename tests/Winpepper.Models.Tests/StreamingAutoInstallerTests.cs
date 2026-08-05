using Winpepper.Models.Tests.ViewModels;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests;

public class StreamingAutoInstallerTests : IDisposable
{
    private readonly string _root;

    public StreamingAutoInstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"autoinstall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private StreamingAutoInstaller CreateInstaller(ModelsTabViewModel.IDownloader downloader) =>
        new(new ModelRegistry(), _root, downloader);

    /// <summary>Lay down the full healthy install: every file at its exact
    /// declared size, plus extraction marker and runtime tree for archives.</summary>
    private void WriteHealthyInstall(bool withExtraction = true)
    {
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName)!;
        var modelDir = Path.Combine(_root, d.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        foreach (var f in d.Files)
        {
            var path = Path.Combine(modelDir, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path)) fs.SetLength(f.SizeBytes); // sparse; exact size
            if (withExtraction && f.ExtractToRelative is { } extractTo)
            {
                File.WriteAllText(path + ".extracted", f.Sha256);
                Directory.CreateDirectory(Path.Combine(modelDir, extractTo));
            }
        }
    }

    [Fact]
    public async Task StartAsync_downloads_the_nemotron_descriptor_when_enabled_and_missing()
    {
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
        Assert.Equal(StreamingAutoInstallStatus.Installed, installer.Status);
    }

    [Fact]
    public async Task StartAsync_skips_the_download_when_streaming_is_disabled()
    {
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);

        await installer.StartAsync(streamingEnabled: false, TestContext.Current.CancellationToken);

        Assert.Empty(fake.DownloadedNames);
        Assert.Equal(StreamingAutoInstallStatus.SkippedStreamingDisabled, installer.Status);
    }

    [Fact]
    public async Task StartAsync_skips_the_download_when_installed_and_extracted()
    {
        // Auto-install runs on EVERY launch, so a healthy install must
        // short-circuit on cheap checks (exact sizes + extraction marker), not
        // re-hash 730 MB per boot. Deep repair stays on the Models card, which
        // always routes through the downloader (commit 1672ae6).
        WriteHealthyInstall();
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Empty(fake.DownloadedNames);
        Assert.Equal(StreamingAutoInstallStatus.Installed, installer.Status);
    }

    [Fact]
    public async Task StartAsync_downloads_when_archive_present_but_extraction_missing()
    {
        // The stuck state commit 1672ae6 healed: archive files complete but
        // extraction failed or the runtime tree was deleted. The auto-install
        // must route through the downloader so EnsureExtracted can heal it.
        WriteHealthyInstall(withExtraction: false);
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task StartAsync_downloads_when_a_file_is_truncated()
    {
        WriteHealthyInstall();
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName)!;
        var gguf = d.Files.First(f => f.ExtractToRelative is null);
        var path = Path.Combine(_root, d.InstallDirRelative, gguf.RelativePath);
        using (var fs = File.Open(path, FileMode.Open)) fs.SetLength(gguf.SizeBytes - 1);

        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);
        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
    }

    [Fact]
    public async Task Concurrent_StartAsync_calls_share_one_download()
    {
        var fake = new BlockingDownloader();
        var installer = CreateInstaller(fake);

        var first = installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);
        await fake.Entered(1);
        var second = installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Same(first, second); // single flight: one in-flight operation
        fake.Release();
        await first;
        await second;

        Assert.Equal(1, fake.EnteredCount);
        Assert.Equal(StreamingAutoInstallStatus.Installed, installer.Status);
    }

    [Fact]
    public async Task StartAsync_failure_is_captured_not_thrown()
    {
        // A failed background install must leave the app fully functional on
        // the batch path: no exception escapes to the caller, the failure is
        // observable via Status/LastError (for logging and the Models card).
        var installer = CreateInstaller(new ThrowingDownloader());

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Equal(StreamingAutoInstallStatus.Failed, installer.Status);
        Assert.Contains("boom", installer.LastError);
    }

    [Fact]
    public async Task Failed_attempt_is_retried_by_a_later_StartAsync()
    {
        // Mirrors the v3 retry policy: no background retry loop; the next
        // attempt (next launch, or a manual install) simply runs again.
        var fake = new ThrowingOnceDownloader();
        var installer = CreateInstaller(fake);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);
        Assert.Equal(StreamingAutoInstallStatus.Failed, installer.Status);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);
        Assert.Equal(StreamingAutoInstallStatus.Installed, installer.Status);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task StartAsync_raises_StatusChanged_through_the_lifecycle()
    {
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);
        var seen = new List<StreamingAutoInstallStatus>();
        installer.StatusChanged += (_, s) => seen.Add(s);

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { StreamingAutoInstallStatus.Installing, StreamingAutoInstallStatus.Installed },
            seen);
    }

    [Fact]
    public async Task A_throwing_StatusChanged_subscriber_cannot_fault_the_install()
    {
        // StartAsync's contract is never-throw; SetStatus must make that
        // mechanical by containing subscriber exceptions. Without that, a
        // throwing subscriber faults RunAsync (and re-faults inside its catch
        // via SetStatus(Failed)), surfacing an exception to the host's
        // fire-and-forget Task.Run.
        var fake = new FakeDownloader();
        var installer = CreateInstaller(fake);
        installer.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber boom");

        await installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);

        // The install itself completed and the status still updated.
        Assert.Equal(new[] { ModelRegistry.StreamingAsrName }, fake.DownloadedNames);
        Assert.Equal(StreamingAutoInstallStatus.Installed, installer.Status);
        Assert.Null(installer.LastError);
    }

    // Note: the "defer auto-install until onboarding completes" policy lives
    // in the Windows-only AppShell wiring (it reads SettingsStore.Load()
    // before calling StartAsync), so it is not testable from this
    // pure-managed suite.

    [Fact]
    public async Task AutoInstall_and_models_card_download_never_run_concurrently()
    {
        // The auto-install shares the Models page's per-downloader operation
        // gate, so opening the Models page and clicking Install during an
        // auto-install can never write the same model files concurrently.
        var fake = new BlockingDownloader();
        var installer = CreateInstaller(fake);
        var vm = new ModelsTabViewModel(new ModelRegistry(), _root, fake,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        var auto = installer.StartAsync(streamingEnabled: true, TestContext.Current.CancellationToken);
        await fake.Entered(1);
        var card = vm.DownloadSelectedAsync(
            new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! },
            TestContext.Current.CancellationToken);

        // If the gate were NOT shared, the card download would enter the
        // downloader while the auto-install is still inside it and the overlap
        // flag would latch immediately; give that wrong path a chance to run.
        await Task.WhenAny(fake.Entered(2), Task.Delay(100, TestContext.Current.CancellationToken));

        fake.Release();
        await auto;
        await card;

        Assert.False(fake.SawOverlap, "auto-install and card download overlapped inside the downloader");
        Assert.Equal(2, fake.EnteredCount);
    }

    private sealed class ThrowingDownloader : ModelsTabViewModel.IDownloader
    {
        public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                  IProgress<DownloadProgress> progress, CancellationToken ct)
            => throw new ModelDownloadException("boom");
    }

    private sealed class ThrowingOnceDownloader : ModelsTabViewModel.IDownloader
    {
        public int Calls { get; private set; }

        public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                  IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Calls++;
            if (Calls == 1) throw new ModelDownloadException("boom");
            return Task.CompletedTask;
        }
    }

    /// <summary>Every call blocks until <see cref="Release"/>; records entry
    /// count, per-count entry signals, and whether two calls ever overlapped.</summary>
    private sealed class BlockingDownloader : ModelsTabViewModel.IDownloader
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _entered = new();
        private int _enteredCount;
        private int _active;

        public bool SawOverlap { get; private set; }
        public int EnteredCount => Volatile.Read(ref _enteredCount);
        public void Release() => _release.TrySetResult();

        public Task Entered(int count)
        {
            lock (_gate) return EnteredTcs(count).Task;
        }

        private TaskCompletionSource EnteredTcs(int count)
        {
            if (!_entered.TryGetValue(count, out var tcs))
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _entered[count] = tcs;
            }
            return tcs;
        }

        public async Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                        IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            lock (_gate)
            {
                if (Interlocked.Increment(ref _active) > 1) SawOverlap = true;
                _enteredCount++;
                EnteredTcs(_enteredCount).TrySetResult();
            }
            try { await _release.Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
