using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.History;

/// <summary>
/// Persistent newest-first archive of dictation sessions. Backed by a single
/// JSON index file plus a tree of WAV files on disk. Pruned according to the
/// current retention policy on every <see cref="Append"/>.
///
/// Thread-safety: all index and WAV mutations are serialized by a
/// process-internal lock, including work supplied to <see cref="WithExclusiveLock"/>.
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 100;

    /// <summary>Fallback/default WAV retention for existing callers.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly EnumerationOptions WavEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private readonly string _root;
    private readonly string _rootFullPath;
    private readonly string _rootPrefix;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly Func<HistoryRetentionPolicy> _policyProvider;
    private readonly Func<DateTime> _utcNow;

    public HistoryStore(string root)
        : this(root, () => HistoryRetentionPolicy.Default, () => DateTime.UtcNow) { }

    public HistoryStore(string root, Func<HistoryRetentionPolicy> policyProvider)
        : this(root, policyProvider, () => DateTime.UtcNow) { }

    // Test seam: tests can pin "now" if they need deterministic boundary checks.
    internal HistoryStore(string root, Func<DateTime> utcNow)
        : this(root, () => HistoryRetentionPolicy.Default, utcNow) { }

    private HistoryStore(
        string root,
        Func<HistoryRetentionPolicy> policyProvider,
        Func<DateTime> utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(policyProvider);
        ArgumentNullException.ThrowIfNull(utcNow);

        _root = root;
        _rootFullPath = Path.GetFullPath(root);
        _rootPrefix = Path.TrimEndingDirectorySeparator(_rootFullPath) + Path.DirectorySeparatorChar;
        _indexPath = Path.Combine(root, "index.json");
        _policyProvider = policyProvider;
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
            return DeserializeAndSort(File.ReadAllText(_indexPath));
        }
        catch (JsonException)
        {
            return new HistoryIndex();
        }
        catch (IOException)
        {
            return new HistoryIndex();
        }
        catch (UnauthorizedAccessException)
        {
            return new HistoryIndex();
        }
    }

    private bool TryLoadStrictUnlocked(out HistoryIndex index)
    {
        try
        {
            index = DeserializeAndSort(File.ReadAllText(_indexPath));
            return true;
        }
        catch (FileNotFoundException)
        {
            index = new HistoryIndex();
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            index = new HistoryIndex();
            return true;
        }
        catch (JsonException)
        {
            index = new HistoryIndex();
            return false;
        }
        catch (IOException)
        {
            index = new HistoryIndex();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            index = new HistoryIndex();
            return false;
        }
    }

    private static HistoryIndex DeserializeAndSort(string json)
    {
        var loaded = JsonSerializer.Deserialize<HistoryIndex>(json, JsonOptions)
            ?? throw new JsonException("index.json is null");
        if (loaded.Entries is null) throw new JsonException("index.json has null entries");
        if (loaded.Entries.Any(e => e is null)) throw new JsonException("index.json has a null entry");
        return loaded with
        {
            Entries = loaded.Entries.OrderByDescending(e => e.CreatedAtUtc).ToList(),
        };
    }

    /// <summary>Insert an entry and apply the current retention policy.</summary>
    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            var idx = LoadUnlocked();
            var combined = idx.Entries.Concat(new[] { entry })
                .OrderByDescending(e => e.CreatedAtUtc)
                .ToList();
            var applied = ApplyPolicy(combined, _policyProvider());
            Save(new HistoryIndex { Entries = applied.Entries });
        }
    }

    /// <summary>Apply an explicit or current retention policy to existing entries.</summary>
    public HistoryPruneResult Prune(HistoryRetentionPolicy? policyOverride = null)
    {
        lock (_gate)
        {
            if (!TryLoadStrictUnlocked(out var index))
                return new HistoryPruneResult { LoadFailed = true };

            var applied = ApplyPolicy(index.Entries, policyOverride ?? _policyProvider());
            try
            {
                Save(new HistoryIndex { Entries = applied.Entries });
                return new HistoryPruneResult
                {
                    DroppedCount = applied.DroppedCount,
                    RetainedAfterFailedDelete = applied.RetainedAfterFailedDelete,
                };
            }
            catch
            {
                return new HistoryPruneResult
                {
                    DroppedCount = applied.DroppedCount,
                    RetainedAfterFailedDelete = applied.RetainedAfterFailedDelete,
                    IndexSaveFailed = true,
                };
            }
        }
    }

    /// <summary>Delete every contained WAV while retaining history entries.</summary>
    public HistoryAudioCleanupResult DeleteAllAudio()
    {
        lock (_gate)
        {
            var deletedCount = 0;
            var failedCount = 0;
            var enumerationFailed = false;

            try
            {
                foreach (var wavPath in Directory.EnumerateFiles(
                             _root, "*.wav", WavEnumerationOptions))
                {
                    if (TryDeleteContainedFile(wavPath, pathIsAbsolute: true)) deletedCount++;
                    else failedCount++;
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The root was deleted wholesale after construction, so no audio remains.
            }
            catch (IOException)
            {
                enumerationFailed = true;
            }
            catch (UnauthorizedAccessException)
            {
                enumerationFailed = true;
            }

            if (!TryLoadStrictUnlocked(out var index))
            {
                return new HistoryAudioCleanupResult
                {
                    DeletedCount = deletedCount,
                    FailedCount = failedCount,
                    EnumerationFailed = enumerationFailed,
                };
            }

            var updated = index.Entries
                .Select(e => !string.IsNullOrEmpty(e.WavRelativePath) && IsWavGoneOrAbsent(e.WavRelativePath)
                    ? e with { WavRelativePath = "" }
                    : e)
                .ToList();

            try
            {
                Save(new HistoryIndex { Entries = updated });
                return new HistoryAudioCleanupResult
                {
                    DeletedCount = deletedCount,
                    FailedCount = failedCount,
                    EnumerationFailed = enumerationFailed,
                };
            }
            catch
            {
                return new HistoryAudioCleanupResult
                {
                    DeletedCount = deletedCount,
                    FailedCount = failedCount,
                    IndexSaveFailed = true,
                    EnumerationFailed = enumerationFailed,
                };
            }
        }
    }

    /// <summary>Return the bytes occupied by contained WAV files.</summary>
    public long ComputeAudioDiskUsageBytes()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root)) return 0;

            long total = 0;
            try
            {
                foreach (var wavPath in Directory.EnumerateFiles(
                             _root, "*.wav", WavEnumerationOptions))
                {
                    if (!TryGetContainedPath(wavPath, pathIsAbsolute: true, out var fullPath)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(fullPath);
                        if ((attributes & FileAttributes.ReparsePoint) == 0 &&
                            !HasReparsePointAncestor(fullPath))
                            total += new FileInfo(fullPath).Length;
                    }
                    catch (IOException)
                    {
                        // Best effort: a racing or inaccessible file contributes nothing.
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Best effort: a racing or inaccessible file contributes nothing.
                    }
                }
            }
            catch (IOException)
            {
                // Return the sum proven so far when enumeration is incomplete.
            }
            catch (UnauthorizedAccessException)
            {
                // Return the sum proven so far when enumeration is incomplete.
            }
            return total;
        }
    }

    /// <summary>Run a store operation under the same lock as archive mutation.</summary>
    public void WithExclusiveLock(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        lock (_gate)
        {
            body();
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

    private AppliedPolicy ApplyPolicy(
        IReadOnlyList<HistoryEntry> entries,
        HistoryRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var ordered = entries.OrderByDescending(e => e.CreatedAtUtc).ToList();
        List<HistoryEntry> fresh;
        List<HistoryEntry> candidates;

        if (policy.MaxAge is TimeSpan maxAge)
        {
            var cutoff = _utcNow() - maxAge;
            fresh = ordered.Where(e => e.CreatedAtUtc >= cutoff).ToList();
            candidates = ordered.Where(e => e.CreatedAtUtc < cutoff).ToList();
        }
        else
        {
            fresh = ordered;
            candidates = new List<HistoryEntry>();
        }

        var maxEntries = Math.Clamp(policy.MaxEntries, 1, 10_000);
        var keep = fresh.Take(maxEntries).ToList();
        candidates.AddRange(fresh.Skip(maxEntries));

        var droppedCount = 0;
        var retainedAfterFailedDelete = 0;
        foreach (var candidate in candidates)
        {
            if (TryDeleteWav(candidate.WavRelativePath)) droppedCount++;
            else
            {
                keep.Add(candidate);
                retainedAfterFailedDelete++;
            }
        }

        return new AppliedPolicy(
            keep.OrderByDescending(e => e.CreatedAtUtc).ToList(),
            droppedCount,
            retainedAfterFailedDelete);
    }

    private void Save(HistoryIndex index)
    {
        var json = JsonSerializer.Serialize(index, JsonOptions);
        AtomicFile.WriteAllText(_indexPath, json);
    }

    private bool TryDeleteWav(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return true;
        return TryDeleteContainedFile(relative, pathIsAbsolute: false);
    }

    private bool TryDeleteContainedFile(string path, bool pathIsAbsolute)
    {
        if (!TryGetContainedPath(path, pathIsAbsolute, out var fullPath)) return false;

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                HasReparsePointAncestor(fullPath))
                return false;
            File.Delete(fullPath);
            return IsFileAbsent(fullPath);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsWavGoneOrAbsent(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return true;
        if (!TryGetContainedPath(relative, pathIsAbsolute: false, out var fullPath)) return false;

        try
        {
            if (HasReparsePointAncestor(fullPath)) return false;
            _ = File.GetAttributes(fullPath);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileAbsent(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool HasReparsePointAncestor(string fullPath)
    {
        var current = Path.GetDirectoryName(fullPath);
        while (current is not null && !string.Equals(current, _rootFullPath, PathComparison))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current);
        }
        return current is null;
    }

    private bool TryGetContainedPath(string path, bool pathIsAbsolute, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!pathIsAbsolute && Path.IsPathRooted(path)) return false;

        try
        {
            fullPath = Path.GetFullPath(pathIsAbsolute ? path : Path.Combine(_rootFullPath, path));
            return fullPath.StartsWith(_rootPrefix, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            fullPath = "";
            return false;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record AppliedPolicy(
        List<HistoryEntry> Entries,
        int DroppedCount,
        int RetainedAfterFailedDelete);
}
