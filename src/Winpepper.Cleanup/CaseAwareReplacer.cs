using System.Text;
using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Applies <see cref="Winpepper.Corrections.CorrectionsData.Replacements"/> as a
/// deterministic case-insensitive whole-word substitution pass. Spec §6.5.
/// The replacement string is emitted verbatim — users configure the canonical
/// spelling, so we don't smear it back into the matched case.
///
/// Overlap handling: when two rules overlap at a position, the longer key wins.
/// Within the same key, leftmost match wins.
/// </summary>
public static class CaseAwareReplacer
{
    public static string Apply(string text, IReadOnlyDictionary<string, string> replacements)
    {
        if (string.IsNullOrEmpty(text) || replacements.Count == 0) return text;

        // Sort keys by length descending so longer keys are attempted first
        // (Regex alternation alone doesn't guarantee longest-match).
        var keys = replacements.Keys
            .Where(k => !string.IsNullOrEmpty(k))
            .OrderByDescending(k => k.Length)
            .ToList();

        if (keys.Count == 0) return text;

        // Build a single regex: \b(?:k1|k2|k3)\b, case-insensitive.
        // \b ensures whole-word matching.
        var pattern = @"\b(?:" + string.Join("|", keys.Select(Regex.Escape)) + @")\b";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var sb = new StringBuilder(text.Length + 64);
        var lastIndex = 0;
        foreach (Match m in rx.Matches(text))
        {
            sb.Append(text, lastIndex, m.Index - lastIndex);

            // Find the matching key (case-insensitive). Prefer the longest matched
            // key that fits at this index (regex already picked one but it doesn't
            // necessarily prefer longest — so re-scan).
            string? bestKey = null;
            foreach (var k in keys)
            {
                if (m.Index + k.Length > text.Length) continue;
                var slice = text.AsSpan(m.Index, k.Length);
                // Leading boundary is guaranteed: candidates start at m.Index,
                // where the regex already enforced \b. The trailing side must be
                // checked here because a candidate longer than the regex match can
                // otherwise end mid-word (e.g. "fresh light" inside "Fresh lighting").
                if (slice.Equals(k.AsSpan(), StringComparison.OrdinalIgnoreCase)
                    && HasTrailingWordBoundary(text, m.Index + k.Length))
                {
                    bestKey = k; // keys are sorted longest-first
                    break;
                }
            }

            if (bestKey is null)
            {
                // Should not happen; defensive fallback.
                sb.Append(m.Value);
                lastIndex = m.Index + m.Length;
            }
            else
            {
                sb.Append(replacements[bestKey]);
                lastIndex = m.Index + bestKey.Length;
            }
        }

        if (lastIndex < text.Length)
            sb.Append(text, lastIndex, text.Length - lastIndex);

        return sb.ToString();
    }

    /// <summary>
    /// True when a match ending at <paramref name="end"/> (exclusive) sits on a
    /// \b-style word boundary: end of string, or not a word-char→word-char run.
    /// </summary>
    private static bool HasTrailingWordBoundary(string text, int end)
    {
        if (end >= text.Length) return true;
        return !IsWordChar(text[end - 1]) || !IsWordChar(text[end]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
