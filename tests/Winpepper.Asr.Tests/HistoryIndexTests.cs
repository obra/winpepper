using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class HistoryIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"history-index-{Guid.NewGuid():N}");

    public HistoryIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Load_RealWorldShape_ParsesEntriesAndIgnoresExtraFields()
    {
        var path = Path.Combine(_dir, "index.json");
        File.WriteAllText(path, """
        {
          "schema": 1,
          "entries": [
            {
              "id": "3f2a1b4c5d6e7f8091a2b3c4d5e6f708",
              "createdAtUtc": "2026-07-26T10:15:30.1234567Z",
              "rawTranscript": "hello world",
              "cleanedText": "Hello world.",
              "wavRelativePath": "2026-07-26/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.wav",
              "durationMs": 2300,
              "asrModelName": "nemotron-streaming-en",
              "cleanupModelName": "none",
              "windowContextUsed": false,
              "windowTitleAtStart": "editor",
              "windowTitleAtInject": "editor",
              "timings": { "recordMs": 2300, "transcribeMs": 450, "cleanupMs": 0, "injectMs": 12, "totalMs": 2800 },
              "transcriptPreview": "Hello world."
            }
          ]
        }
        """);

        var index = HistoryIndex.Load(path);

        index.Schema.ShouldBe(1);
        var entry = index.Entries.Single();
        entry.Id.ShouldBe("3f2a1b4c5d6e7f8091a2b3c4d5e6f708");
        entry.WavRelativePath.ShouldBe("2026-07-26/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.wav");
        entry.RawTranscript.ShouldBe("hello world");
        entry.CleanedText.ShouldBe("Hello world.");
        entry.DurationMs.ShouldBe(2300);
        entry.Timings.TranscribeMs.ShouldBe(450);
    }
}
