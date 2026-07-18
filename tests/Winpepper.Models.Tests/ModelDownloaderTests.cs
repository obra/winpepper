using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDownloaderTests : IDisposable
{
    private readonly string _root;
    public ModelDownloaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    // Progress<T> marshals callbacks via the captured SynchronizationContext.
    // In xUnit there's no SyncContext, so callbacks fire on the ThreadPool
    // independently of the awaiter — the test can read the report list before
    // every callback has run. Use a direct, synchronous IProgress<T> in tests
    // so the report sequence is fully observable at await-return time.
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        public SyncProgress(Action<T> action) => _action = action;
        public void Report(T value) => _action(value);
    }

    private static ModelDescriptor TwoFileDescriptor(string aSha, string bSha) => new()
    {
        Name = "test",
        Kind = ModelKind.Asr,
        DisplayName = "Test",
        InstallDirRelative = "test",
        Files = new[]
        {
            new ModelFile { RelativePath = "a.bin", Url = "https://x/a", Sha256 = aSha, SizeBytes = 5 },
            new ModelFile { RelativePath = "b.bin", Url = "https://x/b", Sha256 = bSha, SizeBytes = 4 },
        },
    };

    [Fact]
    public async Task DownloadAsync_HappyPath_WritesAllFiles_AndReports100Percent()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var reports = new List<DownloadProgress>();
        var progress = new SyncProgress<DownloadProgress>(p => reports.Add(p));
        var dl = new ModelDownloader(fake);

        await dl.DownloadAsync(d, _root, progress, CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
        File.ReadAllText(Path.Combine(_root, "test", "b.bin")).ShouldBe("abcd");

        var completes = reports.Where(p => p.Phase == DownloadPhase.Complete).ToList();
        completes.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task DownloadAsync_ResumesFromPartial()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        var partialDir = Path.Combine(_root, "test");
        Directory.CreateDirectory(partialDir);
        File.WriteAllBytes(Path.Combine(partialDir, "a.bin.partial"),
            System.Text.Encoding.ASCII.GetBytes("hel"));

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        var reports = new List<DownloadProgress>();
        await dl.DownloadAsync(d, _root,
            new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None);

        fake.RequestsFor("https://x/a").Single().RangeStart.ShouldBe(3L);
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();

        var aReports = reports.Where(p => p.FileRelativePath == "a.bin").ToList();
        var downloading = aReports.Where(p => p.Phase == DownloadPhase.Downloading).ToList();
        downloading[0].BytesDownloaded.ShouldBe(3L);
        downloading.Select(p => p.BytesDownloaded)
            .ShouldBe(downloading.Select(p => p.BytesDownloaded).OrderBy(bytes => bytes));
        aReports[^2].Phase.ShouldBe(DownloadPhase.Verifying);
        aReports[^1].Phase.ShouldBe(DownloadPhase.Complete);
        aReports[^1].PercentComplete.ShouldBe(100.0);
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_ThrowsAndDeletesFile()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        var d = TwoFileDescriptor(
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        await Should.ThrowAsync<ModelDownloadException>(() =>
            dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), CancellationToken.None));

        File.Exists(Path.Combine(_root, "test", "a.bin")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadAsync_AlreadyVerified_SkipsDownload()
    {
        var fake = new FakeRangeClient();

        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "test", "a.bin"), "hello");
        File.WriteAllText(Path.Combine(_root, "test", "b.bin"), "abcd");

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        await dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.AllRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_CancellationPropagates()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", new byte[1024 * 1024]);
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));
        fake.DelayPerChunkMs = 50;

        var d = TwoFileDescriptor(
            "0000000000000000000000000000000000000000000000000000000000000000",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), cts.Token));
    }
}

internal sealed class FakeRangeClient : IHttpRangeClient
{
    public sealed record RecordedRequest(string Url, long RangeStart);

    private readonly Dictionary<string, byte[]> _bodies = new();
    private readonly List<RecordedRequest> _requests = new();

    public int DelayPerChunkMs { get; set; }

    public void SetBody(string url, byte[] body) => _bodies[url] = body;

    public IEnumerable<RecordedRequest> AllRequests => _requests;
    public IEnumerable<RecordedRequest> RequestsFor(string url) => _requests.Where(r => r.Url == url);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(
        string url, long startByte,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _requests.Add(new RecordedRequest(url, startByte));
        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");

        const int chunkSize = 64 * 1024;
        var i = (int)startByte;
        while (i < body.Length)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSize, body.Length - i);
            yield return body.AsMemory(i, take);
            i += take;
            if (DelayPerChunkMs > 0)
                await Task.Delay(DelayPerChunkMs, ct).ConfigureAwait(false);
        }
    }

    public Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");
        return Task.FromResult((long)body.Length);
    }
}
