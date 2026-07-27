using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CleanupLatencyBench;

/// <summary>One bench statement: a raw (pre-cleanup) transcript and its stable
/// id (history entry id or eval case name).</summary>
public sealed record BenchStatement(string Id, string Text);

/// <summary>
/// Statement sources for the cleanup latency bench: the statements JSONL format
/// ({"id": "...", "text": "..."} per line) and a READ-ONLY parse of the app's
/// history index.json (id = history entry id, text = RAW transcript -- the
/// pre-cleanup text, never cleanedText). No length filtering anywhere: the
/// bench must observe production behavior including the &lt;4-word bypass.
/// BCL-only so the same file compiles into Winpepper.Cleanup.Tests.
/// </summary>
public static class CleanupBenchStatements
{
    private static readonly JsonSerializerOptions LineOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Serialize statements as JSONL: one {"id","text"} object per line.</summary>
    public static string ToJsonl(IEnumerable<BenchStatement> statements)
    {
        var sb = new StringBuilder();
        foreach (var s in statements)
            sb.Append(JsonSerializer.Serialize(s, LineOpts)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parse JSONL content. Blank lines are skipped; a malformed line
    /// or a line missing id/text throws <see cref="FormatException"/> naming
    /// the 1-based line number.</summary>
    public static IReadOnlyList<BenchStatement> ParseJsonl(string content)
    {
        var result = new List<BenchStatement>();
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new FormatException($"statements line {i + 1}: missing string property 'id'");
                if (!doc.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new FormatException($"statements line {i + 1}: missing string property 'text'");
                result.Add(new BenchStatement(id.GetString()!, text.GetString()!));
            }
            catch (JsonException ex)
            {
                throw new FormatException($"statements line {i + 1}: invalid JSON ({ex.Message})");
            }
        }
        return result;
    }

    /// <summary>
    /// Parse the app's history <c>index.json</c> (camelCase; shape defined by
    /// Winpepper.History.HistoryStore/HistoryEntry) into statements, newest
    /// first by <c>createdAtUtc</c>. The statement text is the entry's RAW
    /// transcript (<c>rawTranscript</c>), never the cleaned text. Extra fields
    /// are ignored; entries keep production defaults for missing fields.
    /// </summary>
    public static IReadOnlyList<BenchStatement> ParseHistoryIndex(string indexJson)
    {
        using var doc = JsonDocument.Parse(indexJson);
        if (!doc.RootElement.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<BenchStatement>();
        }

        var parsed = new List<(DateTime CreatedAtUtc, BenchStatement Statement)>();
        foreach (var entry in entries.EnumerateArray())
        {
            var id = entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String
                ? idProp.GetString()! : "";
            var raw = entry.TryGetProperty("rawTranscript", out var rawProp) && rawProp.ValueKind == JsonValueKind.String
                ? rawProp.GetString()! : "";
            var createdAt = entry.TryGetProperty("createdAtUtc", out var atProp)
                            && atProp.ValueKind == JsonValueKind.String
                            && atProp.TryGetDateTime(out var at)
                ? at : DateTime.MinValue;
            parsed.Add((createdAt, new BenchStatement(id, raw)));
        }

        return parsed.OrderByDescending(p => p.CreatedAtUtc)
                     .Select(p => p.Statement)
                     .ToList();
    }
}
