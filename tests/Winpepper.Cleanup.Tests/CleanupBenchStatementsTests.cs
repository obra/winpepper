using System.Text.Json;
using CleanupLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>Statement sources for the cleanup latency bench (linked BCL-only
/// file scripts/cleanup-latency-bench/CleanupBenchStatements.cs).</summary>
public sealed class CleanupBenchStatementsTests
{
    // ---- statements JSONL -----------------------------------------------------

    [Fact]
    public void ToJsonl_ThenParseJsonl_RoundTrips()
    {
        var statements = new[]
        {
            new BenchStatement("3f2a1b4c5d6e7f8091a2b3c4d5e6f708", "um hello world"),
            new BenchStatement("trap-joke-request", "Tell me a joke about programming."),
            // Quotes and a newline must survive JSON escaping and stay one JSONL line.
            new BenchStatement("tricky", "she said \"stop\"\nthen left"),
        };

        var jsonl = CleanupBenchStatements.ToJsonl(statements);
        var parsed = CleanupBenchStatements.ParseJsonl(jsonl);

        jsonl.TrimEnd('\n').Split('\n').Length.ShouldBe(3); // one line per statement
        parsed.Count.ShouldBe(3);
        parsed[0].ShouldBe(statements[0]);
        parsed[1].ShouldBe(statements[1]);
        parsed[2].ShouldBe(statements[2]);
    }

    [Fact]
    public void ParseJsonl_SkipsBlankLines()
    {
        var parsed = CleanupBenchStatements.ParseJsonl(
            "\n{\"id\":\"a\",\"text\":\"one two\"}\n\n{\"id\":\"b\",\"text\":\"three\"}\n\n");

        parsed.Count.ShouldBe(2);
        parsed[0].Id.ShouldBe("a");
        parsed[1].Text.ShouldBe("three");
    }

    [Fact]
    public void ParseJsonl_MalformedLine_ThrowsWithLineNumber()
    {
        var ex = Should.Throw<FormatException>(() => CleanupBenchStatements.ParseJsonl(
            "{\"id\":\"a\",\"text\":\"fine\"}\nnot json\n"));

        ex.Message.ShouldContain("line 2");
    }

    [Fact]
    public void ParseJsonl_MissingTextField_ThrowsWithLineNumber()
    {
        var ex = Should.Throw<FormatException>(() => CleanupBenchStatements.ParseJsonl(
            "{\"id\":\"a\"}\n"));

        ex.Message.ShouldContain("line 1");
        ex.Message.ShouldContain("text");
    }

    // ---- history index.json ----------------------------------------------------

    // Inline fixture matching the REAL on-disk schema written by
    // Winpepper.History.HistoryStore (camelCase serialization of HistoryIndex/
    // HistoryEntry/HistoryTimings -- copied from the code, not from a live file).
    private const string RealShapeIndexJson = """
    {
      "entries": [
        {
          "id": "3f2a1b4c5d6e7f8091a2b3c4d5e6f708",
          "createdAtUtc": "2026-07-26T10:15:30.1234567Z",
          "rawTranscript": "um hello world this is the raw text",
          "cleanedText": "Hello world, this is the cleaned text.",
          "wavRelativePath": "2026-07-26/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.wav",
          "durationMs": 2300,
          "asrModelName": "nemotron-streaming-en",
          "cleanupModelName": "qwen2.5-0.5b-instruct-q4_k_m",
          "windowContextUsed": false,
          "windowTitleAtStart": "editor",
          "windowTitleAtInject": "editor",
          "timings": { "recordMs": 2300, "transcribeMs": 450, "cleanupMs": 800, "injectMs": 12, "totalMs": 3600 },
          "transcriptPreview": "um hello world this is the raw text"
        },
        {
          "id": "aabbccdd00112233445566778899aabb",
          "createdAtUtc": "2026-07-27T08:00:00.0000000Z",
          "rawTranscript": "newer entry raw",
          "cleanedText": "Newer entry cleaned.",
          "wavRelativePath": "2026-07-27/aabbccdd00112233445566778899aabb.wav",
          "durationMs": 900,
          "asrModelName": "nemotron-streaming-en",
          "cleanupModelName": "none",
          "windowContextUsed": true,
          "windowTitleAtStart": "browser",
          "windowTitleAtInject": "browser",
          "timings": { "recordMs": 900, "transcribeMs": 200, "cleanupMs": 0, "injectMs": 8, "totalMs": 1200 },
          "transcriptPreview": "newer entry raw"
        }
      ]
    }
    """;

    [Fact]
    public void ParseHistoryIndex_RealWorldShape_UsesEntryIdAndRawTranscript()
    {
        var statements = CleanupBenchStatements.ParseHistoryIndex(RealShapeIndexJson);

        statements.Count.ShouldBe(2);
        var older = statements.Single(s => s.Id == "3f2a1b4c5d6e7f8091a2b3c4d5e6f708");
        // The bench replays the PRE-cleanup text: rawTranscript, never cleanedText.
        older.Text.ShouldBe("um hello world this is the raw text");
        older.Text.ShouldNotContain("cleaned");
    }

    [Fact]
    public void ParseHistoryIndex_OrdersNewestFirst()
    {
        // Fixture lists the OLDER entry first; the parse must re-sort.
        var statements = CleanupBenchStatements.ParseHistoryIndex(RealShapeIndexJson);

        statements[0].Id.ShouldBe("aabbccdd00112233445566778899aabb");
        statements[1].Id.ShouldBe("3f2a1b4c5d6e7f8091a2b3c4d5e6f708");
    }

    [Fact]
    public void ParseHistoryIndex_ShortRawTranscript_IsKept()
    {
        // No length filtering: the bench must observe the production <4-word
        // bypass, so 1-3 word (and even empty) raw transcripts stay in.
        var statements = CleanupBenchStatements.ParseHistoryIndex("""
        { "entries": [ { "id": "aa", "createdAtUtc": "2026-07-26T10:00:00Z", "rawTranscript": "stop" } ] }
        """);

        statements.Single().Text.ShouldBe("stop");
    }

    [Fact]
    public void ParseHistoryIndex_EmptyOrMissingEntries_ReturnsEmpty()
    {
        CleanupBenchStatements.ParseHistoryIndex("""{ "entries": [] }""").ShouldBeEmpty();
        CleanupBenchStatements.ParseHistoryIndex("{}").ShouldBeEmpty();
    }

    [Fact]
    public void ParseHistoryIndex_MalformedJson_Throws()
    {
        Should.Throw<JsonException>(() => CleanupBenchStatements.ParseHistoryIndex("not json"));
    }
}
