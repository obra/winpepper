using System.Collections.ObjectModel;

namespace Winpepper.Core.ViewModels;

public sealed class CorrectionsViewModel
{
    private readonly Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> _persist;

    public ObservableCollection<PreferredEntry> Preferred { get; } = new();
    public ObservableCollection<ReplacementEntry> Replacements { get; } = new();

    public CorrectionsViewModel(
        IEnumerable<string> initialPreferred,
        IEnumerable<KeyValuePair<string, string>> initialReplacements,
        Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> persist)
    {
        _persist = persist;
        foreach (var p in initialPreferred) Preferred.Add(new PreferredEntry(p));
        foreach (var r in initialReplacements) Replacements.Add(new ReplacementEntry(r.Key, r.Value));
    }

    public string? AddPreferred(string text)
    {
        var err = ValidatePreferred(text, ignoreSelf: null);
        if (err is not null) return err;
        Preferred.Add(new PreferredEntry(text.Trim()));
        Persist();
        return null;
    }

    public string? AddReplacement(string wrong, string right)
    {
        var err = ValidateReplacement(wrong, right, ignoreSelf: null);
        if (err is not null) return err;
        Replacements.Add(new ReplacementEntry(wrong.Trim(), right.Trim()));
        Persist();
        return null;
    }

    public void RemovePreferred(PreferredEntry e) { Preferred.Remove(e); Persist(); }
    public void RemoveReplacement(ReplacementEntry e) { Replacements.Remove(e); Persist(); }

    public string? ValidatePreferred(string text, PreferredEntry? ignoreSelf)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Cannot be empty.";
        var trimmed = text.Trim();
        if (trimmed.Length < 2) return "Must be at least 2 characters.";
        foreach (var p in Preferred)
        {
            if (ReferenceEquals(p, ignoreSelf)) continue;
            if (string.Equals(p.Text, trimmed, StringComparison.Ordinal)) return "Is a duplicate.";
        }
        return null;
    }

    public string? ValidateReplacement(string wrong, string right, ReplacementEntry? ignoreSelf)
    {
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return "Both sides required.";
        var w = wrong.Trim();
        var r = right.Trim();
        if (w.Length < 2 || r.Length < 2) return "Both sides must be at least 2 characters.";
        if (string.Equals(w, r, StringComparison.Ordinal)) return "Left and right sides are the same.";
        foreach (var existing in Replacements)
        {
            if (ReferenceEquals(existing, ignoreSelf)) continue;
            if (string.Equals(existing.Wrong, w, StringComparison.Ordinal)) return "Is a duplicate.";
        }
        return null;
    }

    public void Persist()
    {
        var p = Preferred.Select(x => x.Text).ToList();
        var r = Replacements.ToDictionary(x => x.Wrong, x => x.Right, StringComparer.Ordinal);
        _persist(p, r);
    }
}
