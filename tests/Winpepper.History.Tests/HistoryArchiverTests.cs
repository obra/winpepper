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
    public void Archive_Skips_WhenIndexCorrupt_LeavesIndexUntouched_AudioPath()
    {
        // A present-but-mangled index must never become a one-entry replacement; the
        // archive is skipped entirely (audio path must not orphan a WAV either).
        var indexPath = Path.Combine(_root, "index.json");
        const string corrupt = "{ nope";
        File.WriteAllText(indexPath, corrupt);
        var store = new HistoryStore(_root);
        var skips = new List<string>();
        var archiver = new HistoryArchiver(store, onArchiveSkipped: skips.Add);

        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
            RawTranscript = "hello",
        });

        entry.ShouldBeNull();
        File.ReadAllText(indexPath).ShouldBe(corrupt);
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        skips.ShouldHaveSingleItem().ShouldContain("index");
    }

    [Fact]
    public void Archive_Skips_WhenIndexCorrupt_TextOnlyPath()
    {
        var indexPath = Path.Combine(_root, "index.json");
        const string corrupt = "{ nope";
        File.WriteAllText(indexPath, corrupt);
        var store = new HistoryStore(_root);
        var skips = new List<string>();
        var archiver = new HistoryArchiver(store, storeAudio: () => false, onArchiveSkipped: skips.Add);

        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16000],
            RawTranscript = "hello",
        });

        entry.ShouldBeNull();
        File.ReadAllText(indexPath).ShouldBe(corrupt);
        skips.ShouldHaveSingleItem().ShouldContain("index");
    }

    [Fact]
    public void Archive_SaveFailureAfterWavWritten_RemovesOrphanAndReports()
    {
        // Root allows reads + the day dir is writable, but the root cannot create the
        // index temp file → the probe passes, the WAV succeeds, the index save fails.
        // The archiver must delete the orphan WAV, report, and return null.
        var indexPath = Path.Combine(_root, "index.json");
        const string indexJson = "{\"entries\":[]}";
        File.WriteAllText(indexPath, indexJson);
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dayDir = Path.Combine(_root, day);
        Directory.CreateDirectory(dayDir);

        Assert.SkipUnless(TryGetUnixMode(_root, out var originalRootMode),
            "Unix permission controls are unavailable on this platform.");
        Assert.SkipUnless(TryGetUnixMode(dayDir, out var originalDayMode),
            "Unix permission controls are unavailable on this platform.");

        HistoryEntry? entry = null;
        var skips = new List<string>();
        try
        {
            // Read-only root: Save's temp-file creation fails; day dir stays writable.
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!CanCreateIn(_root),
                "The current user can still create files in a chmod 500 directory.");

            var store = new HistoryStore(_root);
            var archiver = new HistoryArchiver(store, onArchiveSkipped: skips.Add);

            entry = archiver.Archive(new HistoryArchiveInput
            {
                Samples16k = new float[16000],
                RawTranscript = "hello",
            });
        }
        finally
        {
            File.SetUnixFileMode(_root, originalRootMode);
            File.SetUnixFileMode(dayDir, originalDayMode);
        }

        entry.ShouldBeNull();
        File.ReadAllText(indexPath).ShouldBe(indexJson);
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        skips.ShouldNotBeEmpty();
    }

    [Fact]
    public void Archive_WavWriteFailure_LeavesNoPartialFile_AndReports()
    {
        // Day dir pre-created read-only → WavWriter fails at creation; no partial file may
        // remain, the index must not be touched, and the skip must be reported.
        var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var dayDir = Path.Combine(_root, day);
        Directory.CreateDirectory(dayDir);

        Assert.SkipUnless(TryGetUnixMode(dayDir, out var originalDayMode),
            "Unix permission controls are unavailable on this platform.");

        HistoryEntry? entry = null;
        var skips = new List<string>();
        try
        {
            File.SetUnixFileMode(dayDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!CanCreateIn(dayDir),
                "The current user can still create files in a chmod 500 directory.");

            var store = new HistoryStore(_root);
            var archiver = new HistoryArchiver(store, onArchiveSkipped: skips.Add);

            entry = archiver.Archive(new HistoryArchiveInput
            {
                Samples16k = new float[16000],
                RawTranscript = "hello",
            });
        }
        finally
        {
            File.SetUnixFileMode(dayDir, originalDayMode);
        }

        entry.ShouldBeNull();
        Directory.EnumerateFiles(dayDir, "*.wav").ShouldBeEmpty();
        File.Exists(Path.Combine(_root, "index.json")).ShouldBeFalse();
        skips.ShouldNotBeEmpty();
    }

    private static bool CanCreateIn(string directory)
    {
        var path = Path.Combine(directory, $"probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "probe");
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetUnixMode(string path, out UnixFileMode mode)
    {
        mode = default;
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            mode = File.GetUnixFileMode(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [Fact]
    public void Archive_SkipReported_WhenRootIsSymlink()
    {
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
            var skips = new List<string>();
            var archiver = new HistoryArchiver(store, onArchiveSkipped: skips.Add);

            var entry = archiver.Archive(new HistoryArchiveInput
            {
                Samples16k = new float[16000],
            });

            entry.ShouldBeNull();
            skips.ShouldHaveSingleItem().ShouldContain("junction");
            Directory.GetFileSystemEntries(outsideRoot).ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Archive_SkipsEntirely_WhenRootIsSymlink()
    {
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
    public void Archive_PreplantedLinkedDayDir_DegradesToTextOnly()
    {
        // D6 descendant boundary applied to the WRITE side: a reparse-point day dir must
        // never receive the WAV, and the dictation must not be lost — fall back to the
        // text-only archive (same shape as storeAudio=off).
        var now = DateTime.UtcNow;
        var day = now.ToString("yyyy-MM-dd");
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-archiver-daysymlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var linkedDay = Path.Combine(_root, day);
        Directory.CreateDirectory(_root);
        var linkCreated = false;
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedDay, outsideRoot);
                linkCreated = true;
            }
            catch (Exception)
            {
                // Assert below reports this as an environment skip.
            }
            Assert.SkipUnless(linkCreated, "Directory symlink creation is unavailable.");

            var store = new HistoryStore(_root);
            var archiver = new HistoryArchiver(store, () => now);

            var entry = archiver.Archive(new HistoryArchiveInput
            {
                Samples16k = new float[16000],
                RawTranscript = "hello",
            });

            entry.ShouldNotBeNull();
            entry!.WavRelativePath.ShouldBeEmpty();
            Directory.GetFileSystemEntries(outsideRoot).ShouldBeEmpty();
            store.Load().Entries.ShouldHaveSingleItem().WavRelativePath.ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(linkedDay)) Directory.Delete(linkedDay);
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
