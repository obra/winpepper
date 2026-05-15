using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.Corrections;

/// <summary>
/// Persists <see cref="CorrectionsData"/> to disk atomically. Spec §8.1.
/// Path is typically <c>%LOCALAPPDATA%\winpepper\corrections.json</c> but is
/// injected so tests can use temp paths.
///
/// Concurrency: a single in-process instance per file is expected. The store
/// re-reads the file inside Add* methods so a stale handle never overwrites
/// a concurrent edit by another instance of the same process.
/// </summary>
public sealed class CorrectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public CorrectionStore(string path) { _path = path; }

    public string Path => _path;

    public CorrectionsData Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private CorrectionsData LoadLocked()
    {
        if (!File.Exists(_path)) return CorrectionsData.Empty;

        try
        {
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<CorrectionsData>(json, JsonOptions);
            if (parsed is null) return CorrectionsData.Empty;
            if (parsed.Schema != CorrectionsData.CurrentSchema) return CorrectionsData.Empty;
            return parsed;
        }
        catch (JsonException)
        {
            return CorrectionsData.Empty;
        }
    }

    public void Save(CorrectionsData data)
    {
        lock (_gate)
        {
            SaveLocked(data);
        }
    }

    private void SaveLocked(CorrectionsData data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }

    /// <summary>
    /// Adds a preferred string if it passes validation and isn't already present
    /// (Ordinal comparison). Returns false otherwise.
    /// </summary>
    public bool AddPreferred(string value)
    {
        if (!CorrectionValidation.IsValidPreferred(value)) return false;
        lock (_gate)
        {
            var data = LoadLocked();
            if (data.Preferred.Contains(value, StringComparer.Ordinal)) return false;
            var next = data with
            {
                Preferred = data.Preferred.Concat(new[] { value }).ToArray(),
            };
            SaveLocked(next);
            return true;
        }
    }

    /// <summary>
    /// Adds or overwrites a "wrong → right" replacement. Returns false when
    /// validation fails.
    /// </summary>
    public bool AddReplacement(string wrong, string right)
    {
        if (!CorrectionValidation.IsValidReplacement(wrong, right)) return false;
        lock (_gate)
        {
            var data = LoadLocked();
            var dict = new Dictionary<string, string>(data.Replacements, StringComparer.Ordinal)
            {
                [wrong] = right,
            };
            SaveLocked(data with { Replacements = dict });
            return true;
        }
    }

    /// <summary>
    /// Removes a preferred entry if present. Returns true when something changed.
    /// </summary>
    public bool RemovePreferred(string value)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            var filtered = data.Preferred.Where(s => !string.Equals(s, value, StringComparison.Ordinal)).ToArray();
            if (filtered.Length == data.Preferred.Count) return false;
            SaveLocked(data with { Preferred = filtered });
            return true;
        }
    }

    /// <summary>
    /// Removes a replacement entry if present. Returns true when something changed.
    /// </summary>
    public bool RemoveReplacement(string wrong)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            if (!data.Replacements.ContainsKey(wrong)) return false;
            var dict = new Dictionary<string, string>(data.Replacements, StringComparer.Ordinal);
            dict.Remove(wrong);
            SaveLocked(data with { Replacements = dict });
            return true;
        }
    }
}
