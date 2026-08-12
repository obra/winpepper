using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryArchiverTests : IDisposable
{
    private readonly string _root;
    public HistoryArchiverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"archiver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Archive_WritesWavAndAppendsIndex()
    {
        var now = DateTime.UtcNow;
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, () => now);

        var samples = new float[16000]; // 1s silence
        var input = new HistoryArchiveInput
        {
            Samples16k = samples,
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            AsrModelName = "parakeet-tdt-0.6b-v3",
            CleanupModelName = "qwen2.5-0.5b-instruct-q4_k_m",
            WindowContextUsed = true,
            WindowTitleAtStart = "Notepad",
            WindowTitleAtInject = "Notepad",
            Timings = new HistoryTimings { RecordMs = 1000, TranscribeMs = 200, CleanupMs = 300, InjectMs = 5, TotalMs = 1505 },
        };

        var entry = archiver.Archive(input);

        entry!.RawTranscript.ShouldBe("hello world");
        entry!.CleanedText.ShouldBe("Hello, world.");
        entry!.WavRelativePath.ShouldBe($"{now:yyyy-MM-dd}/{entry!.Id}.wav");
        entry!.DurationMs.ShouldBe(1000); // 16000 samples / 16 kHz = 1 second

        // WAV exists on disk
        File.Exists(Path.Combine(_root, entry!.WavRelativePath)).ShouldBeTrue();

        // Persisted in the index
        store.Load().Entries.Single().Id.ShouldBe(entry!.Id);
    }

    [Fact]
    public void Archive_DurationMs_FromSampleCount()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store);
        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[8000], // 0.5s
            RawTranscript = "",
            CleanedText = "",
        });
        entry!.DurationMs.ShouldBe(500);
    }

    [Fact]
    public void Archive_PartitionsByDay_InUtc()
    {
        var store = new HistoryStore(_root);
        var d1 = new DateTime(2026, 5, 14, 23, 59, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 15, 0, 1, 0, DateTimeKind.Utc);
        var queue = new Queue<DateTime>(new[] { d1, d2 });
        var archiver = new HistoryArchiver(store, () => queue.Dequeue());

        var e1 = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });
        var e2 = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });

        e1!.WavRelativePath.ShouldStartWith("2026-05-14/");
        e2!.WavRelativePath.ShouldStartWith("2026-05-15/");
    }

    [Fact]
    public void Archive_StoreAudioOff_WritesNoWav_PersistsTextOnlyEntry()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, storeAudio: () => false);

        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
        });

        entry.ShouldNotBeNull();
        entry!.WavRelativePath.ShouldBeEmpty();
        entry!.DurationMs.ShouldBe(1000);
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();

        var persisted = store.Load().Entries.ShouldHaveSingleItem();
        persisted.Id.ShouldBe(entry!.Id);
        persisted.WavRelativePath.ShouldBeEmpty();
    }

    [Fact]
    public void Archive_StoreAudioOff_SilentDrop_SkipsArchiveEntirely()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, storeAudio: () => false);

        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
            IsSilentDrop = true,
        });

        entry.ShouldBeNull();
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Archive_SkipsEntirely_WhenRootIsSymlink()
    {
        // D6 fail-closed: routine archiving must also refuse to write through a
        // reparse-point root — no WAV creation and no external index mutation.
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-archiver-rootsymlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var linkRoot = Path.Combine(Path.GetTempPath(), $"winpepper-archiver-rootlink-{Guid.NewGuid():N}");
        var linkCreated = false;
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkRoot, outsideRoot);
                linkCreated = true;
            }
            catch (Exception)
            {
                // Assert below reports this as an environment skip.
            }
            Assert.SkipUnless(linkCreated, "Directory symlink creation is unavailable.");

            var store = new HistoryStore(linkRoot);
            var archiver = new HistoryArchiver(store);

            var entry = archiver.Archive(new HistoryArchiveInput
            {
                Samples16k = new float[16000],
            });

            entry.ShouldBeNull();
            Directory.GetFileSystemEntries(outsideRoot).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Archive_StoreAudioOn_SilentDrop_ArchivesWithWav()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, storeAudio: () => true);

        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
            IsSilentDrop = true,
        });

        entry.ShouldNotBeNull();
        File.Exists(Path.Combine(_root, entry!.WavRelativePath)).ShouldBeTrue();
        store.Load().Entries.ShouldHaveSingleItem().Id.ShouldBe(entry!.Id);
    }

    [Fact]
    public void Archive_StoreAudioGate_ReadLive_PerCall()
    {
        var storeAudio = true;
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, storeAudio: () => storeAudio);

        var first = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });
        storeAudio = false;
        var second = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        File.Exists(Path.Combine(_root, first!.WavRelativePath)).ShouldBeTrue();
        second!.WavRelativePath.ShouldBeEmpty();
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).Count().ShouldBe(1);
    }

    [Fact]
    public void Archive_StoreAudioGate_SampledOncePerCall()
    {
        var sampleCount = 0;
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, storeAudio: () =>
        {
            sampleCount++;
            return true;
        });

        archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });

        sampleCount.ShouldBe(1);
    }

    [Fact]
    public async Task Archive_BlockedByExclusiveLock_CompletesAfterRelease()
    {
        var store = new HistoryStore(_root);
        using var gateHeld = new ManualResetEventSlim();
        using var archiveStarted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var storeAudioInvocations = 0;
        Func<bool> storeAudio = () =>
        {
            if (Interlocked.Increment(ref storeAudioInvocations) == 1)
                archiveStarted.Set();
            return true;
        };
        var archiver = new HistoryArchiver(store, storeAudio: storeAudio);

        var lockTask = Task.Run(() => store.WithExclusiveLock(() =>
        {
            gateHeld.Set();
            release.Wait(TimeSpan.FromSeconds(5), cancellationToken).ShouldBeTrue();
        }), cancellationToken);

        var held = gateHeld.Wait(TimeSpan.FromSeconds(5), cancellationToken);
        if (!held) release.Set();
        held.ShouldBeTrue();

        var archiveTask = Task.Run<HistoryEntry?>(() => archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
        }), cancellationToken);
        try
        {
            archiveStarted.Wait(TimeSpan.FromSeconds(5), cancellationToken).ShouldBeTrue();
            await Task.Delay(250, cancellationToken);
            archiveTask.IsCompleted.ShouldBeFalse();
            Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        }
        finally
        {
            release.Set();
        }

        var entry = await archiveTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await lockTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        entry.ShouldNotBeNull();
        var wavFile = Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldHaveSingleItem();
        // GetFullPath normalizes separators: WavRelativePath uses '/' while
        // EnumerateFiles yields platform separators ('\\' on Windows, where raw
        // string equality fails on the mixed shape) — the gate caught exactly that.
        Path.GetFullPath(wavFile).ShouldBe(Path.GetFullPath(Path.Combine(_root, entry!.WavRelativePath)));
        store.Load().Entries.ShouldHaveSingleItem().Id.ShouldBe(entry!.Id);
    }
}
