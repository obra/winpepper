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
    public void Append_PrunesTo50_AndDeletesPrunedWavFiles()
    {
        var store = new HistoryStore(_root);
        // Pre-create 60 entries with real WAV files on disk.
        for (var i = 0; i < 60; i++)
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
        entries.Count.ShouldBe(50);
        // Newest 50 should be i=10..59
        entries.First().Id.ShouldBe("e59");
        entries.Last().Id.ShouldBe("e10");

        // WAV files for the pruned (oldest) entries should be gone.
        for (var i = 0; i < 10; i++)
            File.Exists(Path.Combine(_root, $"2026-05-15/entry-{i:00}.wav")).ShouldBeFalse();
        // WAV files for the kept entries should still exist.
        for (var i = 10; i < 60; i++)
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
}
