using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.History;

/// <summary>
/// Persistent newest-first archive of dictation sessions. Backed by a single
/// JSON index file plus a tree of WAV files on disk. Pruned to 50 entries
/// on every <see cref="Append"/>; pruned entries' WAVs are deleted.
///
/// Thread-safety: callers are expected to serialize access (one pipeline
/// session at a time per spec §4). The store uses a process-internal lock
/// to defend against accidental concurrent <see cref="Append"/> from the UI
/// thread (e.g., delete-while-finalize).
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 50;

    /// <summary>Spec §5.4: WAVs follow a 30-day rolling retention.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;

    public HistoryStore(string root) : this(root, () => DateTime.UtcNow) { }

    // Test seam: tests can pin "now" if they need deterministic boundary checks.
    internal HistoryStore(string root, Func<DateTime> utcNow)
    {
        _root = root;
        _indexPath = Path.Combine(root, "index.json");
        _utcNow = utcNow;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Absolute path to the history root (= WAV directory).</summary>
    public string Root => _root;

    /// <summary>Resolve a relative WAV path against the history root.</summary>
    public string ResolveWavPath(string relative) => Path.Combine(_root, relative);

    /// <summary>Load all entries, sorted newest-first.</summary>
    public HistoryIndex Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private HistoryIndex LoadUnlocked()
    {
        if (!File.Exists(_indexPath)) return new HistoryIndex();

        try
        {
            var json = File.ReadAllText(_indexPath);
            var loaded = JsonSerializer.Deserialize<HistoryIndex>(json, JsonOptions) ?? new HistoryIndex();
            var sorted = loaded.Entries.OrderByDescending(e => e.CreatedAtUtc).ToList();
            return loaded with { Entries = sorted };
        }
        catch (JsonException)
        {
            return new HistoryIndex();
        }
        catch (IOException)
        {
            return new HistoryIndex();
        }
    }

    /// <summary>Insert <paramref name="entry"/>, prune entries older than <see cref="MaxAge"/>, then cap at <see cref="MaxEntries"/>.</summary>
    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            var idx = LoadUnlocked();
            var combined = idx.Entries.Concat(new[] { entry })
                              .OrderByDescending(e => e.CreatedAtUtc)
                              .ToList();

            // Tier 1: age-based prune (spec §5.4 — 30-day rolling retention).
            // We compute the cutoff once so a multi-entry prune is consistent.
            var cutoff = _utcNow() - MaxAge;
            var fresh = combined.Where(e => e.CreatedAtUtc >= cutoff).ToList();
            var stale = combined.Where(e => e.CreatedAtUtc < cutoff).ToList();

            // Tier 2: count cap (50 entries) over the fresh survivors.
            var keep = fresh.Take(MaxEntries).ToList();
            var dropForCount = fresh.Skip(MaxEntries).ToList();

            foreach (var d in stale)
                TryDeleteWav(d.WavRelativePath);
            foreach (var d in dropForCount)
                TryDeleteWav(d.WavRelativePath);

            Save(new HistoryIndex { Entries = keep });
        }
    }

    /// <summary>Remove the entry with the given id (no-op if absent) and delete its WAV.</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            var idx = LoadUnlocked();
            var match = idx.Entries.FirstOrDefault(e => e.Id == id);
            if (match is null) return;

            TryDeleteWav(match.WavRelativePath);
            var remaining = idx.Entries.Where(e => e.Id != id).ToList();
            Save(new HistoryIndex { Entries = remaining });
        }
    }

    private void Save(HistoryIndex index)
    {
        var json = JsonSerializer.Serialize(index, JsonOptions);
        AtomicFile.WriteAllText(_indexPath, json);
    }

    private void TryDeleteWav(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return;
        try
        {
            var abs = Path.Combine(_root, relative);
            if (File.Exists(abs)) File.Delete(abs);
        }
        catch
        {
            // Best-effort: a locked WAV (rare on Windows) is logged elsewhere.
        }
    }
}
