using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AsrEvalCorpus;

/// <summary>
/// Read-only model of the app's %LOCALAPPDATA%\winpepper\history\index.json
/// (schema 1, camelCase; written by src/Winpepper.History/HistoryStore.cs).
/// Extra fields (windowContextUsed, transcriptPreview, ...) are ignored on read.
/// Deliberately a local DTO instead of a ProjectReference to Winpepper.History:
/// this tool only ever READS the history folder, and the file must stay
/// BCL-only so it compiles into Winpepper.Asr.Tests.
/// </summary>
public sealed record HistoryIndexEntry(
    string Id,
    DateTime CreatedAtUtc,
    string RawTranscript,
    string CleanedText,
    string WavRelativePath,
    int DurationMs,
    string AsrModelName,
    string CleanupModelName,
    ClipTimings Timings);

public sealed record HistoryIndexFile(int Schema, List<HistoryIndexEntry> Entries);

public static class HistoryIndex
{
    public static HistoryIndexFile Load(string indexJsonPath)
        => JsonSerializer.Deserialize<HistoryIndexFile>(File.ReadAllText(indexJsonPath), CorpusJson.Options)
           ?? new HistoryIndexFile(1, new List<HistoryIndexEntry>());
}
