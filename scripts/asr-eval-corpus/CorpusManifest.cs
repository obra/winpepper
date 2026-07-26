using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AsrEvalCorpus;

/// <summary>Per-entry pipeline timings copied from the app's history index.</summary>
public sealed record ClipTimings(int RecordMs, int TranscribeMs, int CleanupMs, int InjectMs, int TotalMs);

/// <summary>
/// One corpus clip. ExpectedSilent and Exclude are curation flags meant to be
/// edited by hand in manifest.json. BCL-only so the same file compiles into
/// Winpepper.Asr.Tests and AsrLatencyBench.
/// </summary>
public sealed record CorpusEntry(
    string Id,
    DateTime CreatedAtUtc,
    int DurationMs,
    string WavPath,
    string RawTranscript,
    string CleanedText,
    string AsrModelName,
    string CleanupModelName,
    ClipTimings Timings)
{
    /// <summary>Reference transcript is empty by definition (the user recorded silence).</summary>
    public bool ExpectedSilent { get; init; }

    /// <summary>Skip this clip entirely (e.g. sensitive content).</summary>
    public bool Exclude { get; init; }
}

public static class CorpusJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

public sealed class CorpusManifest
{
    public int Schema { get; init; } = 1;
    public List<CorpusEntry> Entries { get; init; } = new();

    public static CorpusManifest Load(string path)
        => JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path), CorpusJson.Options)
           ?? new CorpusManifest();

    public static CorpusManifest LoadOrEmpty(string path)
        => File.Exists(path) ? Load(path) : new CorpusManifest();

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, CorpusJson.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
