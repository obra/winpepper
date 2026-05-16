namespace Winpepper.Core.Learning;

/// <summary>
/// Token-level diff between <c>injected</c> (what we typed) and <c>current</c>
/// (what's in the element now). Returns a <see cref="LearningCandidate"/> when
/// exactly one contiguous token-run differs and all pepper-x constraints pass.
/// Spec §8.2 (4).
///
/// Algorithm: tokenize on whitespace, strip the longest common prefix and
/// suffix of tokens, and treat the remaining (non-empty on both sides) middle
/// runs as the <c>wrong</c> / <c>right</c> candidate pair. This lets a misheard
/// multi-word transcription ("chat gbt") map to a single corrected word
/// ("ChatGPT") and vice versa, which a simple position-by-position token diff
/// could not express.
///
/// Constraints applied to the candidate pair:
/// - both sides ≥ <see cref="MinWordLength"/> characters
/// - neither side is whitespace- or punctuation-only
/// - the alphanumeric content differs (punctuation drift alone is not a correction)
/// - not a "first-letter capitalization only" change (autocomplete pattern)
/// - edit distance ≤ <see cref="MaxEditDistanceRatio"/> × max length, rounded down
/// </summary>
public static class LearningDiffAnalyzer
{
    public const int MinWordLength = 3;
    public const double MaxEditDistanceRatio = 0.60;

    public static LearningCandidate? Analyze(string injected, string current)
    {
        ArgumentNullException.ThrowIfNull(injected);
        ArgumentNullException.ThrowIfNull(current);
        if (string.Equals(injected, current, StringComparison.Ordinal)) return null;

        var lhs = Tokenize(injected);
        var rhs = Tokenize(current);

        // Strip common prefix.
        var prefix = 0;
        while (prefix < lhs.Count && prefix < rhs.Count
               && string.Equals(lhs[prefix], rhs[prefix], StringComparison.Ordinal))
            prefix++;

        // Strip common suffix (no overlap with prefix region).
        var suffix = 0;
        while (suffix < lhs.Count - prefix && suffix < rhs.Count - prefix
               && string.Equals(lhs[lhs.Count - 1 - suffix], rhs[rhs.Count - 1 - suffix], StringComparison.Ordinal))
            suffix++;

        var lhsMid = lhs.GetRange(prefix, lhs.Count - prefix - suffix);
        var rhsMid = rhs.GetRange(prefix, rhs.Count - prefix - suffix);

        // Both sides must be non-empty — pure insertion or deletion isn't a correction.
        if (lhsMid.Count == 0 || rhsMid.Count == 0) return null;

        var wrong = string.Join(' ', lhsMid);
        var right = string.Join(' ', rhsMid);

        // No whitespace-only diffs (already excluded by token-empty check, but defensive).
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return null;

        // Min word length applies to both sides.
        if (wrong.Length < MinWordLength || right.Length < MinWordLength) return null;

        // Reject pure-punctuation tokens (no alphanumeric content).
        var wrongCore = StripWordChars(wrong);
        var rightCore = StripWordChars(right);
        if (wrongCore.Length == 0 || rightCore.Length == 0) return null;

        // Reject punctuation-drift-only diffs: if the alphanumeric content is
        // identical, the only difference is punctuation and that's not a misheard
        // correction.
        if (string.Equals(wrongCore, rightCore, StringComparison.Ordinal)) return null;

        // Reject "first-letter capitalization only" — looks like autocomplete.
        if (IsFirstLetterCapitalizationOnly(wrong, right)) return null;

        // Edit distance bound: ≤ floor(60 % of the longer side's length).
        var maxLen = Math.Max(wrong.Length, right.Length);
        var dist = LevenshteinDistance.Compute(wrong, right);
        if (dist == 0) return null;
        if (dist > Math.Floor(maxLen * MaxEditDistanceRatio)) return null;

        return new LearningCandidate(wrong, right);
    }

    private static List<string> Tokenize(string s)
    {
        var parts = s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return new List<string>(parts);
    }

    private static string StripWordChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsFirstLetterCapitalizationOnly(string a, string b)
    {
        if (a.Length != b.Length) return false;
        if (a.Length == 0) return false;
        if (!char.IsLetter(a[0]) || !char.IsLetter(b[0])) return false;
        if (char.ToLowerInvariant(a[0]) != char.ToLowerInvariant(b[0])) return false;
        if (a[0] == b[0]) return false;
        for (var i = 1; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
