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

    private static ModelDescriptor OneFileDescriptor(long sizeBytes = 5) => new()
    {
        Name = "test",
        Kind = ModelKind.Asr,
        DisplayName = "Test",
        InstallDirRelative = "test",
        Files = new[]
        {
            new ModelFile
            {
                RelativePath = "a.bin",
                Url = "https://x/a",
                Sha256 = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
                SizeBytes = sizeBytes,
            },
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
    public async Task DownloadAsync_TransientFailure_RetriesFromPartialLength()
    {
        var fake = new FakeRangeClient { FailuresRemaining = 1 };
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));

        var partialDir = Path.Combine(_root, "test");
        Directory.CreateDirectory(partialDir);
        File.WriteAllText(Path.Combine(partialDir, "a.bin.partial"), "hel");

        var dl = new ModelDownloader(fake);
        await dl.DownloadAsync(OneFileDescriptor(), _root,
            new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.RequestsFor("https://x/a").Select(request => request.RangeStart)
            .ShouldBe(new long[] { 3, 3 });
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
    }

    [Fact]
    public async Task DownloadAsync_StalledStream_RetriesFromReceivedLength()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenBlockingStream("he"u8.ToArray()), startByte));
        var delays = new List<TimeSpan>();
        var options = new ModelDownloaderOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(25),
            RetryDelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };

        var dl = new ModelDownloader(fake, options);
        await dl.DownloadAsync(OneFileDescriptor(), _root,
            new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.RequestsFor("https://x/a").Select(request => request.RangeStart)
            .ShouldBe(new long[] { 0, 2 });
        delays.ShouldBe(new[] { TimeSpan.FromSeconds(1) });
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
    }

    [Fact]
    public async Task DownloadAsync_CompletedPartialAfterIdleTimeout_VerifiesWithoutAnotherRangeRequest()
    {
        var fake = new FakeRangeClient();
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenBlockingStream("hello"u8.ToArray()), startByte));
        var options = new ModelDownloaderOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(25),
            RetryDelayAsync = (_, _) => Task.CompletedTask,
        };

        var dl = new ModelDownloader(fake, options);
        await dl.DownloadAsync(OneFileDescriptor(), _root,
            new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.RequestsFor("https://x/a").Single().RangeStart.ShouldBe(0);
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadAsync_ThreeTransientAttempts_UsesBoundedBackoffAndPreservesPartial()
    {
        var fake = new FakeRangeClient();
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenThrowStream("h"u8.ToArray()), startByte));
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenThrowStream("e"u8.ToArray()), startByte));
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenThrowStream("l"u8.ToArray()), startByte));
        var delays = new List<TimeSpan>();
        var options = new ModelDownloaderOptions
        {
            RetryDelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        };

        var dl = new ModelDownloader(fake, options);
        await Should.ThrowAsync<ModelDownloadException>(() =>
            dl.DownloadAsync(OneFileDescriptor(), _root,
                new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None));

        fake.RequestsFor("https://x/a").Select(request => request.RangeStart)
            .ShouldBe(new long[] { 0, 1, 2 });
        delays.ShouldBe(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) });
        File.ReadAllText(Path.Combine(_root, "test", "a.bin.partial")).ShouldBe("hel");
    }

    [Fact]
    public async Task DownloadAsync_IgnoredRange_TruncatesBeforeWritingFullResponse()
    {
        var fake = new FakeRangeClient();
        fake.EnqueueResponse("https://x/a", _ =>
            new HttpRangeResponse(new MemoryStream("hello"u8.ToArray()), contentStartByte: 0));
        var partialDir = Path.Combine(_root, "test");
        Directory.CreateDirectory(partialDir);
        File.WriteAllText(Path.Combine(partialDir, "a.bin.partial"), "hel");

        var dl = new ModelDownloader(fake);
        await dl.DownloadAsync(OneFileDescriptor(), _root,
            new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.RequestsFor("https://x/a").Single().RangeStart.ShouldBe(3);
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
    }

    [Fact]
    public async Task DownloadAsync_IncompatibleRange_IsRejectedWithoutChangingPartial()
    {
        var fake = new FakeRangeClient();
        fake.EnqueueResponse("https://x/a", _ =>
            new HttpRangeResponse(new MemoryStream("llo"u8.ToArray()), contentStartByte: 2));
        var partialDir = Path.Combine(_root, "test");
        Directory.CreateDirectory(partialDir);
        File.WriteAllText(Path.Combine(partialDir, "a.bin.partial"), "hel");

        var dl = new ModelDownloader(fake);
        await Should.ThrowAsync<ModelDownloadException>(() =>
            dl.DownloadAsync(OneFileDescriptor(), _root,
                new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None));

        fake.RequestsFor("https://x/a").Count().ShouldBe(1);
        File.ReadAllText(Path.Combine(_root, "test", "a.bin.partial")).ShouldBe("hel");
    }

    [Fact]
    public async Task DownloadAsync_DeclaredSizeMismatch_DoesNotPromotePartial()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));

        var dl = new ModelDownloader(fake);
        await Should.ThrowAsync<ModelDownloadException>(() =>
            dl.DownloadAsync(OneFileDescriptor(sizeBytes: 4), _root,
                new SyncProgress<DownloadProgress>(_ => { }), CancellationToken.None));

        File.Exists(Path.Combine(_root, "test", "a.bin")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();
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
        using var cts = new CancellationTokenSource();
        fake.EnqueueResponse("https://x/a", startByte =>
            new HttpRangeResponse(new DataThenCancelStream("he"u8.ToArray(), cts), startByte));
        var delays = new List<TimeSpan>();
        var dl = new ModelDownloader(fake, new ModelDownloaderOptions
        {
            RetryDelayAsync = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        });
        await Should.ThrowAsync<OperationCanceledException>(() =>
            dl.DownloadAsync(OneFileDescriptor(), _root,
                new SyncProgress<DownloadProgress>(_ => { }), cts.Token));

        var partialPath = Path.Combine(_root, "test", "a.bin.partial");
        File.Exists(partialPath).ShouldBeTrue();
        File.ReadAllText(partialPath).ShouldBe("he");
        fake.RequestsFor("https://x/a").Count().ShouldBe(1);
        delays.ShouldBeEmpty();
    }
}

internal sealed class FakeRangeClient : IHttpRangeClient
{
    public sealed record RecordedRequest(string Url, long RangeStart);

    private readonly Dictionary<string, byte[]> _bodies = new();
    private readonly Dictionary<string, Queue<Func<long, HttpRangeResponse>>> _responses = new();
    private readonly List<RecordedRequest> _requests = new();

    public int FailuresRemaining { get; set; }

    public void SetBody(string url, byte[] body) => _bodies[url] = body;

    public void EnqueueResponse(string url, Func<long, HttpRangeResponse> responseFactory)
    {
        if (!_responses.TryGetValue(url, out var queue))
        {
            queue = new Queue<Func<long, HttpRangeResponse>>();
            _responses[url] = queue;
        }
        queue.Enqueue(responseFactory);
    }

    public IEnumerable<RecordedRequest> AllRequests => _requests;
    public IEnumerable<RecordedRequest> RequestsFor(string url) => _requests.Where(r => r.Url == url);

    public Task<HttpRangeResponse> GetRangeAsync(string url, long startByte, CancellationToken ct)
    {
        _requests.Add(new RecordedRequest(url, startByte));
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            throw new IOException("Transient test failure");
        }

        if (_responses.TryGetValue(url, out var queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Dequeue()(startByte));
        }

        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");
        var remaining = body.AsMemory((int)startByte).ToArray();
        return Task.FromResult(new HttpRangeResponse(new MemoryStream(remaining), startByte));
    }

    public Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");
        return Task.FromResult((long)body.Length);
    }
}

internal abstract class ScriptedReadStream : Stream
{
    protected ScriptedReadStream(byte[] data) => Data = data;

    protected byte[] Data { get; }
    protected bool DataReturned { get; private set; }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!DataReturned)
        {
            DataReturned = true;
            Data.CopyTo(buffer);
            return ValueTask.FromResult(Data.Length);
        }
        return ReadAfterDataAsync(cancellationToken);
    }

    protected abstract ValueTask<int> ReadAfterDataAsync(CancellationToken cancellationToken);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class DataThenBlockingStream(byte[] data) : ScriptedReadStream(data)
{
    protected override async ValueTask<int> ReadAfterDataAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
}

internal sealed class DataThenThrowStream(byte[] data) : ScriptedReadStream(data)
{
    protected override ValueTask<int> ReadAfterDataAsync(CancellationToken cancellationToken) =>
        ValueTask.FromException<int>(new IOException("Transient test failure"));
}

internal sealed class DataThenCancelStream(byte[] data, CancellationTokenSource cts) : ScriptedReadStream(data)
{
    protected override ValueTask<int> ReadAfterDataAsync(CancellationToken cancellationToken)
    {
        cts.Cancel();
        return ValueTask.FromCanceled<int>(cancellationToken);
    }
}
