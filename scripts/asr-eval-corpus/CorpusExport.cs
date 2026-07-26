using System;
using System.Collections.Generic;
using System.Linq;

namespace AsrEvalCorpus;

public sealed record ExportItem(HistoryIndexEntry Source, CorpusEntry Entry);

public sealed record ExportPlan(IReadOnlyList<ExportItem> ToAdd, int SkippedExisting);

public static class CorpusExport
{
    /// <summary>
    /// Plans an export: history entries whose id is not yet in the corpus
    /// manifest, newest first, optionally limited to the most recent
    /// <paramref name="take"/> new clips. Ids are the app's stable history
    /// entry ids, so re-running never duplicates a clip.
    /// </summary>
    public static ExportPlan BuildPlan(
        IReadOnlyList<HistoryIndexEntry> history, CorpusManifest existing, int? take = null)
    {
        var known = new HashSet<string>(existing.Entries.Select(e => e.Id), StringComparer.Ordinal);
        var fresh = history.Where(h => !known.Contains(h.Id))
            .OrderByDescending(h => h.CreatedAtUtc)
            .ToList();
        var skipped = history.Count - fresh.Count;
        if (take is int n)
            fresh = fresh.Take(n).ToList();
        var toAdd = fresh.Select(h => new ExportItem(h, new CorpusEntry(
            h.Id, h.CreatedAtUtc, h.DurationMs, $"clips/{h.Id}.wav",
            h.RawTranscript, h.CleanedText, h.AsrModelName, h.CleanupModelName, h.Timings))).ToList();
        return new ExportPlan(toAdd, skipped);
    }
}
