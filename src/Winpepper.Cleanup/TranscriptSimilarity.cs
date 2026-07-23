using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Pure content-word similarity used by <see cref="CleanupRunner"/> to detect
/// wholesale replacement / severe truncation by the cleanup LLM (Bug-3 fix-(i)).
/// Content words exclude the fillers and self-correction phrases a legitimate
/// cleanup is allowed to drop, so removing "um"/"scratch that" does not look
/// like content loss.
/// </summary>
public static class TranscriptSimilarity
{
    // Multi-word phrases removed before tokenizing. Ordered longest-first so a
    // shorter phrase never eats part of a longer one.
    private static readonly string[] Phrases =
    {
        "no let me start over", "let me start over",
        "scratch that", "never mind", "start over", "no wait",
        "you know", "sort of", "kind of",
    };

    private static readonly HashSet<string> FillerWords =
        new(StringComparer.Ordinal) { "um", "uh", "like", "basically", "literally" };

    /// <summary>Lowercase, strip filler/self-correction phrases, split on any
    /// non-alphanumeric run, and drop single-word fillers.</summary>
    public static IReadOnlyList<string> ContentWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var lower = text.ToLowerInvariant();
        foreach (var p in Phrases)
            lower = lower.Replace(p, " ");

        var result = new List<string>();
        foreach (var tok in Regex.Split(lower, "[^a-z0-9]+"))
        {
            if (tok.Length == 0) continue;
            if (FillerWords.Contains(tok)) continue;
            result.Add(tok);
        }
        return result;
    }

    /// <summary>Fraction of the raw transcript's unique content words that
    /// survive into the cleaned text. 1.0 when the raw has no content words.</summary>
    public static double RetentionRatio(string raw, string cleaned)
    {
        var rawWords = new HashSet<string>(ContentWords(raw), StringComparer.Ordinal);
        if (rawWords.Count == 0) return 1.0;
        var cleanedWords = new HashSet<string>(ContentWords(cleaned), StringComparer.Ordinal);
        var kept = rawWords.Count(w => cleanedWords.Contains(w));
        return (double)kept / rawWords.Count;
    }

    /// <summary>Whitespace-delimited token count of the trimmed text.</summary>
    public static int WordCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
