using System.Text.RegularExpressions;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Detects when a cleanup model answered the dictation instead of cleaning it
/// (port of ghost-pepper's isChatbotResponse). Pure string heuristic so it can
/// be unit-tested without a model. Signals, each guarded against the case
/// where the user actually dictated the trigger:
///  1. Assistant-phrase opening ("Sure,", "Here's", "Certainly", ...).
///  2. "as an AI" anywhere.
///  3. Output-length blowup (cleanup removes fillers; it never triples text).
///  4. A numbered/bulleted list appearing when the input had none.
/// </summary>
public static class ChatbotResponseHeuristic
{
    private static readonly string[] AssistantOpeners =
    {
        "sure", "here's", "here is", "here are", "of course", "certainly",
        "absolutely", "no problem", "great question", "good question",
        "i'd be happy", "i would be happy", "i'm happy to", "i can help",
        "i can't help", "i cannot help", "how can i help", "as an ai",
        "i'm sorry", "i am sorry",
    };

    private static readonly Regex ListLine =
        new(@"^\s*(\d+[\.\)]|[-*\u2022])\s+", RegexOptions.Compiled);

    public static bool IsChatbotResponse(string input, string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;

        var inNorm = Normalize(input);
        var outNorm = Normalize(output);

        // 1. Assistant-phrase opening the user didn't dictate themselves.
        foreach (var opener in AssistantOpeners)
        {
            if (StartsWithPhrase(outNorm, opener) && !StartsWithPhrase(inNorm, opener))
                return true;
        }

        // 2. "as an AI" anywhere - never legitimate cleanup unless spoken.
        if (outNorm.Contains("as an ai", StringComparison.Ordinal) &&
            !inNorm.Contains("as an ai", StringComparison.Ordinal))
        {
            return true;
        }

        // 3. Length blowup: cleanup only removes fillers and adds punctuation.
        if (output.Trim().Length > input.Trim().Length * 2.75 + 20) return true;

        // 4. Spurious list: >= 2 list-shaped lines when the input had none.
        if (CountListLines(output) >= 2 && CountListLines(input) == 0) return true;

        return false;
    }

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace('\u2019', '\'');

    /// <summary>Prefix match with a word boundary so "sure" never matches "surely".</summary>
    private static bool StartsWithPhrase(string normalized, string phrase) =>
        normalized.StartsWith(phrase, StringComparison.Ordinal) &&
        (normalized.Length == phrase.Length || !char.IsLetter(normalized[phrase.Length]));

    private static int CountListLines(string s)
    {
        var count = 0;
        foreach (var line in s.Split('\n'))
        {
            if (ListLine.IsMatch(line)) count++;
        }
        return count;
    }
}
