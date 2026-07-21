using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Deterministic correction for the ASR (parakeet-tdt-0.6b-v3) mishearing the
/// app's own name. "winpepper" is transcribed as "wheat pepper" / "win pepper"
/// / "wind pepper" / "when pepper". Maps a small fixed set of mishearings back
/// to the app name, choosing capitalization from surrounding context:
/// capitalize ("Winpepper") when the match is sentence-initial or the
/// immediately preceding word is capitalized; otherwise lowercase
/// ("winpepper"). Part 2.
///
/// Tradeoff: this is an unconditional whole-word replacement, so a legitimate
/// culinary phrase like "wheat pepper soup recipe" is also rewritten to
/// "Winpepper soup recipe". Accepted collateral: the app name is dictated far
/// more often than wheat-pepper cookery, and the rule stays conservative — a
/// small fixed list, deliberately NOT a general vocabulary-hint system.
/// </summary>
public static class AppNameCorrector
{
    // Common mishearings of "winpepper". Data-driven and intentionally minimal.
    private static readonly string[] Mishearings =
    {
        "wheat pepper", "win pepper", "wind pepper", "when pepper",
    };

    // \b(?:wheat pepper|win pepper|wind pepper|when pepper)\b, case-insensitive.
    private static readonly Regex Pattern = new(
        @"\b(?:" + string.Join("|", Mishearings.Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Pattern.Replace(text, m =>
            ShouldCapitalize(text, m.Index) ? "Winpepper" : "winpepper");
    }

    private static bool ShouldCapitalize(string text, int matchStart)
    {
        // Walk back over whitespace immediately before the match.
        int i = matchStart - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;

        if (i < 0) return true;                                  // start of text
        if (text[i] is '.' or '!' or '?' or ':' or ';') return true; // sentence-initial

        // Mirror the capitalization of the preceding word's first letter.
        while (i >= 0 && !char.IsWhiteSpace(text[i])) i--;
        return char.IsUpper(text[i + 1]);
    }
}
