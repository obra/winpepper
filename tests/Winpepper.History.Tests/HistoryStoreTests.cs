using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _root;

    public HistoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"winpepper-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyIndex()
    {
        var store = new HistoryStore(_root);
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Append_Then_Load_ReturnsEntry()
    {
        var store = new HistoryStore(_root);
        var entry = new HistoryEntry { Id = "a", RawTranscript = "alpha" };
        store.Append(entry);
        store.Load().Entries.Single().Id.ShouldBe("a");
    }

    [Fact]
    public void Append_NewestFirst()
    {
        var store = new HistoryStore(_root);
        var older = new HistoryEntry { Id = "older", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var newer = new HistoryEntry { Id = "newer", CreatedAtUtc = DateTime.UtcNow };
        store.Append(older);
        store.Append(newer);
        var entries = store.Load().Entries;
        entries[0].Id.ShouldBe("newer");
        entries[1].Id.ShouldBe("older");
    }

    [Fact]
    public void Append_PrunesTo100_AndDeletesPrunedWavFiles()
    {
        var store = new HistoryStore(_root);
        // Pre-create 110 entries with real WAV files on disk.
        for (var i = 0; i < 110; i++)
        {
            var rel = $"2026-05-15/entry-{i:00}.wav";
            var abs = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, $"wav-{i}");
            store.Append(new HistoryEntry
            {
                Id = $"e{i:00}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i), // newer entries have larger i
                WavRelativePath = rel,
            });
        }

        var entries = store.Load().Entries;
        entries.Count.ShouldBe(100);
        // Newest 100 should be i=10..109
        entries.First().Id.ShouldBe("e109");
        entries.Last().Id.ShouldBe("e10");

        // WAV files for the pruned (oldest) entries should be gone.
        for (var i = 0; i < 10; i++)
            File.Exists(Path.Combine(_root, $"2026-05-15/entry-{i:00}.wav")).ShouldBeFalse();
        // WAV files for the kept entries should still exist.
        for (var i = 10; i < 110; i++)
            File.Exists(Path.Combine(_root, $"2026-05-15/entry-{i:00}.wav")).ShouldBeTrue();
    }

    [Fact]
    public void Append_PrunesEntriesOlderThanMaxAge_AndDeletesTheirWavs()
    {
        // Spec §5.4 line 150: WAVs follow a 30-day rolling retention.
        var store = new HistoryStore(_root);
        var oldRel = "2026-04-01/old.wav";
        var oldAbs = Path.Combine(_root, oldRel);
        Directory.CreateDirectory(Path.GetDirectoryName(oldAbs)!);
        File.WriteAllText(oldAbs, "stale-wav");

        // 31 days old — past the 30-day retention.
        store.Append(new HistoryEntry
        {
            Id = "old",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-31),
            WavRelativePath = oldRel,
        });
        // Touching the store again triggers the age-based prune.
        store.Append(new HistoryEntry
        {
            Id = "fresh",
            CreatedAtUtc = DateTime.UtcNow,
            WavRelativePath = "",
        });

        var entries = store.Load().Entries;
        entries.Select(e => e.Id).ShouldNotContain("old");
        entries.Select(e => e.Id).ShouldContain("fresh");
        File.Exists(oldAbs).ShouldBeFalse();
    }

    [Fact]
    public void Append_KeepsEntriesAtTheMaxAgeBoundary()
    {
        // Exactly 29 days old — must survive.
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry
        {
            Id = "boundary",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-29),
            WavRelativePath = "",
        });
        store.Load().Entries.Single().Id.ShouldBe("boundary");
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{ not valid json");
        var store = new HistoryStore(_root);
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Load_NullEntriesIndex_ReturnsEmptyIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\": null}");
        var store = new HistoryStore(_root);

        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Load_NullEntryElement_ReturnsEmptyIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\":[null]}");
        var store = new HistoryStore(_root);

        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Delete_RemovesEntryAndWav()
    {
        var store = new HistoryStore(_root);
        var rel = "2026-05-15/keep.wav";
        var abs = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "wav");

        var entry = new HistoryEntry { Id = "k", WavRelativePath = rel };
        store.Append(entry);
        store.Delete("k");

        store.Load().Entries.ShouldBeEmpty();
        File.Exists(abs).ShouldBeFalse();
    }

    [Fact]
    public void Delete_UnknownId_NoOp()
    {
        var store = new HistoryStore(_root);
        Should.NotThrow(() => store.Delete("never-existed"));
    }

    [Fact]
    public void Append_DoesNotLeaveTempFile()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "a" });
        Directory.GetFiles(_root, "index.json.tmp-*").ShouldBeEmpty();
    }

    [Fact]
    public void Append_CustomCountCap_KeepsNewestAndDeletesOldWavs()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 3, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);

        for (var i = 0; i < 5; i++)
        {
            var rel = $"custom-cap/entry-{i}.wav";
            CreateWav(rel, $"wav-{i}");
            store.Append(new HistoryEntry
            {
                Id = $"e{i}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
                WavRelativePath = rel,
            });
        }

        store.Load().Entries.Select(e => e.Id).ShouldBe(["e4", "e3", "e2"]);
        File.Exists(Path.Combine(_root, "custom-cap/entry-0.wav")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "custom-cap/entry-1.wav")).ShouldBeFalse();
        File.Exists(Path.Combine(_root, "custom-cap/entry-2.wav")).ShouldBeTrue();
    }

    [Fact]
    public void Append_CustomAgeCap_PrunesEightDayOldAndKeepsFiveDayOld()
    {
        var store = new HistoryStore(_root, () => new HistoryRetentionPolicy
        {
            MaxEntries = 100,
            MaxAgeDays = 7,
        });
        var oldRel = "custom-age/old.wav";
        var freshRel = "custom-age/fresh.wav";
        CreateWav(oldRel, "old");
        CreateWav(freshRel, "fresh");

        store.Append(new HistoryEntry
        {
            Id = "old",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-8),
            WavRelativePath = oldRel,
        });
        store.Append(new HistoryEntry
        {
            Id = "fresh",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-5),
            WavRelativePath = freshRel,
        });

        store.Load().Entries.Select(e => e.Id).ShouldBe(["fresh"]);
        File.Exists(Path.Combine(_root, oldRel)).ShouldBeFalse();
        File.Exists(Path.Combine(_root, freshRel)).ShouldBeTrue();
    }

    [Fact]
    public void Append_UnlimitedAge_KeepsOldEntryButStillEnforcesCountCap()
    {
        var store = new HistoryStore(_root, () => new HistoryRetentionPolicy
        {
            MaxEntries = 2,
            MaxAgeDays = null,
        });

        store.Append(new HistoryEntry { Id = "ancient", CreatedAtUtc = DateTime.UtcNow.AddDays(-400) });
        store.Append(new HistoryEntry { Id = "middle", CreatedAtUtc = DateTime.UtcNow.AddDays(-300) });
        store.Append(new HistoryEntry { Id = "oldest", CreatedAtUtc = DateTime.UtcNow.AddDays(-500) });

        store.Load().Entries.Select(e => e.Id).ShouldBe(["middle", "ancient"]);
    }

    [Fact]
    public void Prune_CustomCountCap_DropsEntriesAndDeletesTheirWavs()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 10, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);
        for (var i = 0; i < 5; i++)
        {
            var rel = $"prune-count/entry-{i}.wav";
            CreateWav(rel, $"wav-{i}");
            store.Append(new HistoryEntry
            {
                Id = $"e{i}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
                WavRelativePath = rel,
            });
        }

        policy = policy with { MaxEntries = 2 };
        var result = store.Prune();

        result.DroppedCount.ShouldBe(3);
        result.IndexSaveFailed.ShouldBeFalse();
        store.Load().Entries.Select(e => e.Id).ShouldBe(["e4", "e3"]);
        for (var i = 0; i < 3; i++)
            File.Exists(Path.Combine(_root, $"prune-count/entry-{i}.wav")).ShouldBeFalse();
    }

    [Fact]
    public void Prune_CustomAgeCap_DropsOnlyStaleEntry()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 10, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);
        var oldRel = "prune-age/old.wav";
        var freshRel = "prune-age/fresh.wav";
        CreateWav(oldRel, "old");
        CreateWav(freshRel, "fresh");
        store.Append(new HistoryEntry
        {
            Id = "old",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-8),
            WavRelativePath = oldRel,
        });
        store.Append(new HistoryEntry
        {
            Id = "fresh",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-5),
            WavRelativePath = freshRel,
        });

        policy = policy with { MaxAgeDays = 7 };
        var result = store.Prune();

        result.DroppedCount.ShouldBe(1);
        result.IndexSaveFailed.ShouldBeFalse();
        store.Load().Entries.Select(e => e.Id).ShouldBe(["fresh"]);
        File.Exists(Path.Combine(_root, oldRel)).ShouldBeFalse();
        File.Exists(Path.Combine(_root, freshRel)).ShouldBeTrue();
    }

    [Fact]
    public void Prune_UnlimitedAge_StillEnforcesCountCap()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 10, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);
        store.Append(new HistoryEntry { Id = "new", CreatedAtUtc = DateTime.UtcNow.AddDays(-300) });
        store.Append(new HistoryEntry { Id = "old", CreatedAtUtc = DateTime.UtcNow.AddDays(-400) });

        policy = policy with { MaxEntries = 1 };
        var result = store.Prune();

        result.DroppedCount.ShouldBe(1);
        result.IndexSaveFailed.ShouldBeFalse();
        store.Load().Entries.Single().Id.ShouldBe("new");
    }

    [Fact]
    public void Prune_CorruptIndex_ReturnsZerosAndLeavesBytesUntouched()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var corrupt = "{ definitely not json";
        File.WriteAllText(indexPath, corrupt);
        var store = new HistoryStore(_root, () => new HistoryRetentionPolicy { MaxEntries = 1 });

        var result = store.Prune();

        result.ShouldBe(new HistoryPruneResult { LoadFailed = true });
        File.ReadAllText(indexPath).ShouldBe(corrupt);
    }

    [Fact]
    public void Prune_NullIndex_ReturnsLoadFailedAndLeavesBytesUntouched()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var originalBytes = "null"u8.ToArray();
        File.WriteAllBytes(indexPath, originalBytes);
        var store = new HistoryStore(_root);

        var result = store.Prune();

        result.ShouldBe(new HistoryPruneResult { LoadFailed = true });
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void Prune_NullEntriesIndex_BailsWithoutWriting()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\": null}");
        var originalBytes = File.ReadAllBytes(indexPath);
        var store = new HistoryStore(_root);

        var result = store.Prune();

        result.DroppedCount.ShouldBe(0);
        result.IndexSaveFailed.ShouldBeFalse();
        result.LoadFailed.ShouldBeTrue();
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void Prune_NullEntryElement_BailsWithoutWriting()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\":[null]}");
        var originalBytes = File.ReadAllBytes(indexPath);
        var store = new HistoryStore(_root);

        var result = store.Prune();

        result.ShouldBe(new HistoryPruneResult { LoadFailed = true });
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void Prune_UnreadableIndex_ReturnsZerosAndLeavesFileUntouched()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var contents = "{\"schema\":1,\"entries\":[]}";
        File.WriteAllText(indexPath, contents);
        var store = new HistoryStore(_root);
        Assert.SkipUnless(TryGetUnixMode(indexPath, out var originalMode),
            "Unix permission controls are unavailable on this platform.");

        try
        {
            File.SetUnixFileMode(indexPath, UnixFileMode.None);
            Assert.SkipUnless(!CanReadFile(indexPath),
                "The current user can still read a chmod 000 file.");

            var result = store.Prune();

            result.ShouldBe(new HistoryPruneResult { LoadFailed = true });
        }
        finally
        {
            File.SetUnixFileMode(indexPath, originalMode);
        }

        File.ReadAllText(indexPath).ShouldBe(contents);
    }

    [Fact]
    public void Prune_BlockedIndexSave_ReportsFailureWithoutChangingIndex()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 10, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);
        for (var i = 0; i < 5; i++)
            store.Append(new HistoryEntry { Id = $"e{i}", CreatedAtUtc = DateTime.UtcNow.AddSeconds(i) });
        policy = policy with { MaxEntries = 2 };
        var indexPath = Path.Combine(_root, "index.json");
        var originalIndex = File.ReadAllText(indexPath);
        Assert.SkipUnless(TryGetUnixMode(_root, out var originalMode),
            "Unix permission controls are unavailable on this platform.");
        HistoryPruneResult? result = null;

        try
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!CanCreateFile(_root),
                "The current user can still write in a chmod 500 directory.");
            result = store.Prune();
        }
        finally
        {
            File.SetUnixFileMode(_root, originalMode);
        }

        result.ShouldNotBeNull();
        result.DroppedCount.ShouldBe(3);
        result.IndexSaveFailed.ShouldBeTrue();
        File.ReadAllText(indexPath).ShouldBe(originalIndex);
    }

    [Fact]
    public void Prune_ResistingWav_IsReportedKeptAndRetriedTruthfully()
    {
        var policy = new HistoryRetentionPolicy { MaxEntries = 3, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);
        var resistingRel = "prune-resisting/blocked.wav";
        var resistingPath = CreateWav(resistingRel, "blocked");
        store.Append(new HistoryEntry
        {
            Id = "oldest",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            WavRelativePath = resistingRel,
        });
        store.Append(new HistoryEntry
        {
            Id = "middle",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        });
        store.Append(new HistoryEntry { Id = "newest", CreatedAtUtc = DateTime.UtcNow });

        var resistingDirectory = Path.GetDirectoryName(resistingPath)!;
        var probePath = Path.Combine(resistingDirectory, "probe.tmp");
        File.WriteAllText(probePath, "probe");
        Assert.SkipUnless(TryGetUnixMode(resistingDirectory, out var originalDirectoryMode),
            "Unix permission controls are unavailable on this platform.");
        var originalFileMode = File.GetUnixFileMode(resistingPath);
        HistoryPruneResult? first = null;
        policy = policy with { MaxEntries = 2 };

        try
        {
            File.SetUnixFileMode(resistingPath, UnixFileMode.UserRead);
            File.SetUnixFileMode(resistingDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!TryDeleteExistingFile(probePath),
                "The current user can still delete files from a read-only directory.");

            first = store.Prune();
        }
        finally
        {
            File.SetUnixFileMode(resistingDirectory, originalDirectoryMode);
            if (File.Exists(resistingPath)) File.SetUnixFileMode(resistingPath, originalFileMode);
        }

        first.ShouldNotBeNull();
        first.DroppedCount.ShouldBe(0);
        first.RetainedAfterFailedDelete.ShouldBe(1);
        first.IndexSaveFailed.ShouldBeFalse();
        store.Load().Entries.Select(e => e.Id).ShouldBe(["newest", "middle", "oldest"]);
        File.Exists(resistingPath).ShouldBeTrue();

        var second = store.Prune();

        second.DroppedCount.ShouldBe(1);
        second.RetainedAfterFailedDelete.ShouldBe(0);
        second.IndexSaveFailed.ShouldBeFalse();
        store.Load().Entries.Select(e => e.Id).ShouldBe(["newest", "middle"]);
        File.Exists(resistingPath).ShouldBeFalse();
    }

    [Fact]
    public void DeleteAllAudio_DeletesIndexedAndOrphanWavs_ClearsRefs_AndIsIdempotent()
    {
        var store = new HistoryStore(_root);
        for (var i = 0; i < 3; i++)
        {
            var rel = $"delete-all/entry-{i}.wav";
            CreateWav(rel, $"wav-{i}");
            store.Append(new HistoryEntry
            {
                Id = $"e{i}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i),
                WavRelativePath = rel,
            });
        }
        CreateWav("delete-all/orphan.wav", "orphan");

        var first = store.DeleteAllAudio();

        first.DeletedCount.ShouldBe(4);
        first.FailedCount.ShouldBe(0);
        first.IndexSaveFailed.ShouldBeFalse();
        first.EnumerationFailed.ShouldBeFalse();
        Directory.GetFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        store.Load().Entries.Count.ShouldBe(3);
        store.Load().Entries.ShouldAllBe(e => e.WavRelativePath == "");

        store.DeleteAllAudio().ShouldBe(new HistoryAudioCleanupResult());
    }

    [Fact]
    public void DeleteAllAudio_CorruptIndex_StillSweepsWavsAndLeavesIndexUntouched()
    {
        var indexPath = Path.Combine(_root, "index.json");
        var corrupt = "{ not valid";
        File.WriteAllText(indexPath, corrupt);
        CreateWav("corrupt-index/one.wav", "one");
        CreateWav("corrupt-index/two.wav", "two");
        var store = new HistoryStore(_root);

        var result = store.DeleteAllAudio();

        result.DeletedCount.ShouldBe(2);
        result.FailedCount.ShouldBe(0);
        result.IndexSaveFailed.ShouldBeFalse();
        result.EnumerationFailed.ShouldBeFalse();
        File.ReadAllText(indexPath).ShouldBe(corrupt);
    }

    [Fact]
    public void DeleteAllAudio_NullIndex_StillSweepsWavsAndLeavesBytesUntouched()
    {
        var wavPath = CreateWav("null-index/orphan.wav", "orphan");
        var indexPath = Path.Combine(_root, "index.json");
        var originalBytes = "null"u8.ToArray();
        File.WriteAllBytes(indexPath, originalBytes);
        var store = new HistoryStore(_root);

        var result = store.DeleteAllAudio();

        result.DeletedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(0);
        result.IndexSaveFailed.ShouldBeFalse();
        result.EnumerationFailed.ShouldBeFalse();
        File.Exists(wavPath).ShouldBeFalse();
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void DeleteAllAudio_NullEntriesIndex_StillSweepsButDoesNotThrowAndDoesNotRewriteIndex()
    {
        var wavPath = CreateWav("null-entries/orphan.wav", "orphan");
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\": null}");
        var originalBytes = File.ReadAllBytes(indexPath);
        var store = new HistoryStore(_root);

        var result = store.DeleteAllAudio();

        result.DeletedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(0);
        result.IndexSaveFailed.ShouldBeFalse();
        result.EnumerationFailed.ShouldBeFalse();
        File.Exists(wavPath).ShouldBeFalse();
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void DeleteAllAudio_NullEntryElement_SweepsButDoesNotThrowAndDoesNotRewrite()
    {
        var wavPath = CreateWav("null-entry/orphan.wav", "orphan");
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{\"entries\":[null]}");
        var originalBytes = File.ReadAllBytes(indexPath);
        var store = new HistoryStore(_root);

        var result = store.DeleteAllAudio();

        result.DeletedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(0);
        result.IndexSaveFailed.ShouldBeFalse();
        result.EnumerationFailed.ShouldBeFalse();
        File.Exists(wavPath).ShouldBeFalse();
        File.ReadAllBytes(indexPath).ShouldBe(originalBytes);
    }

    [Fact]
    public void DeleteAllAudio_ResistingWav_IsCountedAndRetriedTruthfully()
    {
        var store = new HistoryStore(_root);
        var resistingRel = "resisting/blocked.wav";
        var otherRel = "writable/other.wav";
        var resistingPath = CreateWav(resistingRel, "blocked");
        var otherPath = CreateWav(otherRel, "other");
        store.Append(new HistoryEntry { Id = "blocked", WavRelativePath = resistingRel });
        store.Append(new HistoryEntry { Id = "other", WavRelativePath = otherRel });
        var resistingDirectory = Path.GetDirectoryName(resistingPath)!;
        var probePath = Path.Combine(resistingDirectory, "probe.tmp");
        File.WriteAllText(probePath, "probe");
        Assert.SkipUnless(TryGetUnixMode(resistingDirectory, out var originalDirectoryMode),
            "Unix permission controls are unavailable on this platform.");
        var originalFileMode = File.GetUnixFileMode(resistingPath);
        HistoryAudioCleanupResult? first = null;

        try
        {
            File.SetUnixFileMode(resistingPath, UnixFileMode.UserRead);
            File.SetUnixFileMode(resistingDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!TryDeleteExistingFile(probePath),
                "The current user can still delete files from a read-only directory.");

            first = store.DeleteAllAudio();
        }
        finally
        {
            File.SetUnixFileMode(resistingDirectory, originalDirectoryMode);
            if (File.Exists(resistingPath)) File.SetUnixFileMode(resistingPath, originalFileMode);
        }

        first.ShouldNotBeNull();
        first.DeletedCount.ShouldBe(1);
        first.FailedCount.ShouldBe(1);
        first.IndexSaveFailed.ShouldBeFalse();
        File.Exists(resistingPath).ShouldBeTrue();
        File.Exists(otherPath).ShouldBeFalse();
        store.Load().Entries.Single(e => e.Id == "blocked").WavRelativePath.ShouldBe(resistingRel);
        store.Load().Entries.Single(e => e.Id == "other").WavRelativePath.ShouldBe("");

        var second = store.DeleteAllAudio();

        second.DeletedCount.ShouldBe(1);
        second.FailedCount.ShouldBe(0);
        second.IndexSaveFailed.ShouldBeFalse();
        File.Exists(resistingPath).ShouldBeFalse();
        store.Load().Entries.Single(e => e.Id == "blocked").WavRelativePath.ShouldBe("");
    }

    [Fact]
    public void DeleteAllAudio_BlockedIndexSave_ReportsFailureAndLeavesRefsUnchanged()
    {
        var store = new HistoryStore(_root);
        var rel = "blocked-save/entry.wav";
        var wavPath = CreateWav(rel, "wav");
        store.Append(new HistoryEntry { Id = "entry", WavRelativePath = rel });
        var indexPath = Path.Combine(_root, "index.json");
        var originalIndex = File.ReadAllText(indexPath);
        Assert.SkipUnless(TryGetUnixMode(_root, out var originalMode),
            "Unix permission controls are unavailable on this platform.");
        HistoryAudioCleanupResult? result = null;

        try
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!CanCreateFile(_root),
                "The current user can still write in a chmod 500 directory.");
            result = store.DeleteAllAudio();
        }
        finally
        {
            File.SetUnixFileMode(_root, originalMode);
        }

        result.ShouldNotBeNull();
        result.DeletedCount.ShouldBe(1);
        result.IndexSaveFailed.ShouldBeTrue();
        File.Exists(wavPath).ShouldBeFalse();
        File.ReadAllText(indexPath).ShouldBe(originalIndex);
        store.Load().Entries.Single().WavRelativePath.ShouldBe(rel);
    }

    [Fact]
    public void DeleteAllAudio_InaccessibleSubtree_ReportsEnumerationFailure()
    {
        var store = new HistoryStore(_root);
        var inaccessibleDirectory = Path.Combine(_root, "inaccessible");
        Directory.CreateDirectory(inaccessibleDirectory);
        File.WriteAllText(Path.Combine(inaccessibleDirectory, "hidden.wav"), "hidden");
        Assert.SkipUnless(TryGetUnixMode(inaccessibleDirectory, out var originalMode),
            "Unix permission controls are unavailable on this platform.");
        HistoryAudioCleanupResult? result = null;

        try
        {
            File.SetUnixFileMode(inaccessibleDirectory, UnixFileMode.None);
            Assert.SkipUnless(!CanEnumerateDirectory(inaccessibleDirectory),
                "The current user can still enumerate a chmod 000 directory.");

            result = store.DeleteAllAudio();
        }
        finally
        {
            File.SetUnixFileMode(inaccessibleDirectory, originalMode);
        }

        result.ShouldNotBeNull();
        result.EnumerationFailed.ShouldBeTrue();

        var emptyRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-empty-{Guid.NewGuid():N}");
        try
        {
            var emptyResult = new HistoryStore(emptyRoot).DeleteAllAudio();
            emptyResult.EnumerationFailed.ShouldBeFalse();
            emptyResult.ShouldBe(new HistoryAudioCleanupResult());
        }
        finally
        {
            if (Directory.Exists(emptyRoot)) Directory.Delete(emptyRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteAllAudio_InaccessibleRoot_ReportsEnumerationFailure()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"winpepper-history-parent-{Guid.NewGuid():N}");
        var historyRoot = Path.Combine(parent, "history");
        Directory.CreateDirectory(parent);

        try
        {
            var store = new HistoryStore(historyRoot);
            Assert.SkipUnless(TryGetUnixMode(parent, out var originalMode),
                "Unix permission controls are unavailable on this platform.");
            HistoryAudioCleanupResult? result = null;

            try
            {
                File.SetUnixFileMode(parent, UnixFileMode.None);
                Assert.SkipUnless(!Directory.Exists(historyRoot) && !CanEnumerateDirectory(historyRoot),
                    "The current user can still access a directory through a chmod 000 parent.");

                result = store.DeleteAllAudio();
            }
            finally
            {
                File.SetUnixFileMode(parent, originalMode);
            }

            result.ShouldNotBeNull();
            result.EnumerationFailed.ShouldBeTrue();
            result.ShouldNotBe(new HistoryAudioCleanupResult());
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void DeleteAllAudio_DoesNotFollowDirectorySymlinkOutsideRoot()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-outside-{Guid.NewGuid():N}");
        var linkPath = Path.Combine(_root, "linked-away");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "outside.wav");
        File.WriteAllText(outsideWav, "outside");
        var linkCreated = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
                linkCreated = true;
            }
            catch (Exception)
            {
                // Assert below reports this as an environment skip.
            }
            Assert.SkipUnless(linkCreated, "Directory symlink creation is unavailable.");

            var result = new HistoryStore(_root).DeleteAllAudio();

            result.ShouldBe(new HistoryAudioCleanupResult());
            File.Exists(outsideWav).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void AppendPrune_DoesNotDeleteIndexedWavThroughDirectorySymlink()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-indexed-{Guid.NewGuid():N}");
        var linkPath = Path.Combine(_root, "linked-indexed");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "outside.wav");
        File.WriteAllText(outsideWav, "outside");
        var linkCreated = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
                linkCreated = true;
            }
            catch (Exception)
            {
                // Assert below reports this as an environment skip.
            }
            Assert.SkipUnless(linkCreated, "Directory symlink creation is unavailable.");
            var store = new HistoryStore(_root, () => new HistoryRetentionPolicy
            {
                MaxEntries = 1,
                MaxAgeDays = null,
            });
            store.Append(new HistoryEntry
            {
                Id = "linked",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                WavRelativePath = "linked-indexed/outside.wav",
            });

            store.Append(new HistoryEntry { Id = "new", CreatedAtUtc = DateTime.UtcNow });

            File.Exists(outsideWav).ShouldBeTrue();
            store.Load().Entries.Select(e => e.Id).ShouldContain("linked");
        }
        finally
        {
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void DeleteAllAudio_RefusesWhenRootItselfIsSymlink()
    {
        // D6 physical-safety boundary must include the root: a junctioned/symlinked
        // history root must fail closed for every destructive op — no WAV deletion AND
        // no external index.json mutation (no create, no replace).
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-rootsymlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "outside.wav");
        File.WriteAllText(outsideWav, "outside");
        var linkRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-rootlink-{Guid.NewGuid():N}");
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

            var result = new HistoryStore(linkRoot).DeleteAllAudio();

            File.Exists(outsideWav).ShouldBeTrue();
            result.DeletedCount.ShouldBe(0);
            Directory.GetFileSystemEntries(outsideRoot).ShouldBe([outsideWav]);
        }
        finally
        {
            if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void Prune_DoesNotDeleteThroughSymlinkedRoot()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-rootsymlink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "outside.wav");
        File.WriteAllText(outsideWav, "outside");
        // Seed the external index directly (never through the store) so the test proves
        // that no destructive op mutates the external target at all.
        var indexPath = Path.Combine(outsideRoot, "index.json");
        const string indexJson =
            "{\"entries\":[" +
            "{\"id\":\"old\",\"createdAtUtc\":\"2020-01-01T00:00:00Z\",\"wavRelativePath\":\"outside.wav\"}," +
            "{\"id\":\"new\",\"createdAtUtc\":\"2026-01-01T00:00:00Z\"}]}";
        File.WriteAllText(indexPath, indexJson);
        var linkRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-rootlink-{Guid.NewGuid():N}");
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

            var store = new HistoryStore(linkRoot, () => new HistoryRetentionPolicy
            {
                MaxEntries = 1,
                MaxAgeDays = null,
            });

            var result = store.Prune();

            result.LoadFailed.ShouldBeTrue();
            result.DroppedCount.ShouldBe(0);
            File.Exists(outsideWav).ShouldBeTrue();
            File.ReadAllText(indexPath).ShouldBe(indexJson);
            Directory.GetFileSystemEntries(outsideRoot).OrderBy(p => p).ShouldBe([indexPath, outsideWav]);
        }
        finally
        {
            if (Directory.Exists(linkRoot)) Directory.Delete(linkRoot);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void TraversalWavPath_IsRefusedAndEntryRetainedByAppendPruneAndPrune()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "evil.wav");
        File.WriteAllText(outsideWav, "evil");
        var relativeEscape = $"../{Path.GetFileName(outsideRoot)}/evil.wav";
        var policy = new HistoryRetentionPolicy { MaxEntries = 1, MaxAgeDays = null };
        var store = new HistoryStore(_root, () => policy);

        try
        {
            store.Append(new HistoryEntry
            {
                Id = "escape",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                WavRelativePath = relativeEscape,
            });
            store.Append(new HistoryEntry { Id = "new", CreatedAtUtc = DateTime.UtcNow });

            File.Exists(outsideWav).ShouldBeTrue();
            store.Load().Entries.Select(e => e.Id).ShouldContain("escape");

            var result = store.Prune();

            result.DroppedCount.ShouldBe(0);
            result.IndexSaveFailed.ShouldBeFalse();
            File.Exists(outsideWav).ShouldBeTrue();
            store.Load().Entries.Select(e => e.Id).ShouldContain("escape");
        }
        finally
        {
            Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void ComputeAudioDiskUsageBytes_SumsOnlyContainedWavs()
    {
        var store = new HistoryStore(_root);
        CreateWav("usage/ten.wav", new string('a', 10));
        CreateWav("usage/nested/twenty.WAV", new string('b', 20));
        var nonWav = Path.Combine(_root, "usage/not-a-wav.txt");
        File.WriteAllText(nonWav, new string('c', 50));

        store.ComputeAudioDiskUsageBytes().ShouldBe(30);
    }

    [Fact]
    public void ComputeAudioDiskUsageBytes_EmptyOrMissingRoot_ReturnsZero()
    {
        var store = new HistoryStore(_root);
        store.ComputeAudioDiskUsageBytes().ShouldBe(0);

        Directory.Delete(_root, recursive: true);

        store.ComputeAudioDiskUsageBytes().ShouldBe(0);
    }

    [Fact]
    public void ComputeAudioDiskUsageBytes_DoesNotCountSymlinkedAwayContent()
    {
        var outsideRoot = Path.Combine(Path.GetTempPath(), $"winpepper-history-usage-{Guid.NewGuid():N}");
        var linkPath = Path.Combine(_root, "linked-usage");
        Directory.CreateDirectory(outsideRoot);
        var outsideWav = Path.Combine(outsideRoot, "outside.wav");
        File.WriteAllText(outsideWav, new string('x', 40));
        var linkCreated = false;

        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
                linkCreated = true;
            }
            catch (Exception)
            {
                // Assert below reports this as an environment skip.
            }
            Assert.SkipUnless(linkCreated, "Directory symlink creation is unavailable.");
            CreateWav("inside.wav", new string('i', 5));

            new HistoryStore(_root).ComputeAudioDiskUsageBytes().ShouldBe(5);
        }
        finally
        {
            if (Directory.Exists(linkPath)) Directory.Delete(linkPath);
            if (Directory.Exists(outsideRoot)) Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void WithExclusiveLock_RunsBody()
    {
        var store = new HistoryStore(_root);
        var invoked = false;

        store.WithExclusiveLock(() => invoked = true);

        invoked.ShouldBeTrue();
    }

    private string CreateWav(string relative, string contents)
    {
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents);
        return absolute;
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

    private static bool CanReadFile(string path)
    {
        try
        {
            _ = File.ReadAllText(path);
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

    private static bool CanCreateFile(string directory)
    {
        var probe = Path.Combine(directory, $"write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
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

    private static bool TryDeleteExistingFile(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
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

    private static bool CanEnumerateDirectory(string path)
    {
        try
        {
            _ = Directory.GetFileSystemEntries(path);
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
}
