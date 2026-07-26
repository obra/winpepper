using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorpusManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"corpus-manifest-{Guid.NewGuid():N}");

    public CorpusManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Best-effort temp-dir teardown: swallow EVERYTHING (UnauthorizedAccessException
        // etc., not just IOException) -- cleanup must never fail the test run.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static CorpusEntry Entry(string id, bool expectedSilent = false, bool exclude = false) =>
        new(id, new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc), 2300, $"clips/{id}.wav",
            "raw text", "Cleaned text.", "nemotron-streaming-en", "none",
            new ClipTimings(2300, 450, 0, 12, 2800))
        { ExpectedSilent = expectedSilent, Exclude = exclude };

    [Fact]
    public void SaveThenLoad_RoundTrips_EntriesAndFlags()
    {
        var path = Path.Combine(_dir, "manifest.json");
        var manifest = new CorpusManifest();
        manifest.Entries.Add(Entry("aaa", expectedSilent: true));
        manifest.Entries.Add(Entry("bbb", exclude: true));

        manifest.Save(path);
        var loaded = CorpusManifest.Load(path);

        loaded.Schema.ShouldBe(1);
        loaded.Entries.Count.ShouldBe(2);
        loaded.Entries[0].Id.ShouldBe("aaa");
        loaded.Entries[0].ExpectedSilent.ShouldBeTrue();
        loaded.Entries[0].Exclude.ShouldBeFalse();
        loaded.Entries[1].Exclude.ShouldBeTrue();
        loaded.Entries[1].Timings.TranscribeMs.ShouldBe(450);
    }

    [Fact]
    public void Load_HandEditedCamelCaseJson_ReadsCurationFlags()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, """
        {
          "schema": 1,
          "entries": [
            {
              "id": "abc123",
              "createdAtUtc": "2026-07-26T10:15:30Z",
              "durationMs": 1500,
              "wavPath": "clips/abc123.wav",
              "rawTranscript": "um hello",
              "cleanedText": "Hello.",
              "asrModelName": "nemotron-streaming-en",
              "cleanupModelName": "none",
              "timings": { "recordMs": 1500, "transcribeMs": 300, "cleanupMs": 0, "injectMs": 10, "totalMs": 1900 },
              "expectedSilent": true,
              "exclude": false
            }
          ]
        }
        """);

        var loaded = CorpusManifest.Load(path);

        loaded.Entries.Single().ExpectedSilent.ShouldBeTrue();
        loaded.Entries.Single().WavPath.ShouldBe("clips/abc123.wav");
        loaded.Entries.Single().Timings.TotalMs.ShouldBe(1900);
    }

    [Fact]
    public void LoadOrEmpty_MissingFile_ReturnsEmptyManifest()
    {
        var loaded = CorpusManifest.LoadOrEmpty(Path.Combine(_dir, "does-not-exist.json"));

        loaded.Schema.ShouldBe(1);
        loaded.Entries.ShouldBeEmpty();
    }
}
