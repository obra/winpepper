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
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, () => new DateTime(2026, 5, 15, 10, 0, 0, DateTimeKind.Utc));

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

        entry.RawTranscript.ShouldBe("hello world");
        entry.CleanedText.ShouldBe("Hello, world.");
        entry.WavRelativePath.ShouldBe($"2026-05-15/{entry.Id}.wav");
        entry.DurationMs.ShouldBe(1000); // 16000 samples / 16 kHz = 1 second

        // WAV exists on disk
        File.Exists(Path.Combine(_root, entry.WavRelativePath)).ShouldBeTrue();

        // Persisted in the index
        store.Load().Entries.Single().Id.ShouldBe(entry.Id);
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
        entry.DurationMs.ShouldBe(500);
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

        e1.WavRelativePath.ShouldStartWith("2026-05-14/");
        e2.WavRelativePath.ShouldStartWith("2026-05-15/");
    }
}
