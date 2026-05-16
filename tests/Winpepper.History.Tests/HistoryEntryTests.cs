using Shouldly;
using System.Text.Json;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryEntryTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var e = new HistoryEntry();
        e.Id.ShouldNotBeNullOrEmpty();
        e.RawTranscript.ShouldBe("");
        e.CleanedText.ShouldBe("");
        e.WavRelativePath.ShouldBe("");
        e.DurationMs.ShouldBe(0);
        e.AsrModelName.ShouldBe("");
        e.CleanupModelName.ShouldBe("");
        e.WindowContextUsed.ShouldBeFalse();
        e.WindowTitleAtStart.ShouldBe("");
        e.WindowTitleAtInject.ShouldBe("");
    }

    [Fact]
    public void RoundTrips_Through_Json()
    {
        var original = new HistoryEntry
        {
            Id = "deadbeef",
            CreatedAtUtc = new DateTime(2026, 5, 15, 10, 30, 0, DateTimeKind.Utc),
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            WavRelativePath = "2026-05-15/deadbeef.wav",
            DurationMs = 1234,
            AsrModelName = "parakeet-tdt-0.6b-v3",
            CleanupModelName = "qwen2.5-0.5b-instruct-q4_k_m",
            WindowContextUsed = true,
            WindowTitleAtStart = "Notepad",
            WindowTitleAtInject = "Notepad",
            Timings = new HistoryTimings { RecordMs = 1200, TranscribeMs = 350, CleanupMs = 410, InjectMs = 12, TotalMs = 1990 },
        };

        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<HistoryEntry>(json)!;

        loaded.ShouldBe(original);
    }

    [Fact]
    public void TranscriptPreview_TrimsAndTruncatesTo80Chars()
    {
        var e = new HistoryEntry { RawTranscript = "   " + new string('x', 200) + "   " };
        var preview = e.TranscriptPreview;
        preview.Length.ShouldBeLessThanOrEqualTo(80);
        preview.ShouldStartWith("xxxx");
        preview.ShouldNotContain("   ");
    }

    [Fact]
    public void TranscriptPreview_ShortText_ReturnedUnchanged()
    {
        var e = new HistoryEntry { RawTranscript = "hi" };
        e.TranscriptPreview.ShouldBe("hi");
    }
}
