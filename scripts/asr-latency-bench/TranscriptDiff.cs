using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AsrLatencyBench;

public sealed record DiffSummary(
    bool TrivialOnly,
    int BatchWordCount,
    int StreamWordCount,
    IReadOnlyList<string> WordDiffs)
{
    public string Describe() => TrivialOnly
        ? $"IDENTICAL after case/punctuation/whitespace normalization ({BatchWordCount} words)"
        : $"{WordDiffs.Count} word-level diffs (batch {BatchWordCount} words, stream {StreamWordCount} words): {string.Join(" ", WordDiffs)}";
}

/// <summary>
/// Word-level transcript comparison. "Trivial" = equal after lowercasing,
/// stripping punctuation (apostrophes kept), and collapsing whitespace —
/// the acceptance bar for streamed-vs-batch transcripts. Anything else is
/// reported honestly as -word (batch only) / +word (stream only) via LCS.
/// BCL-only so the same file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class TranscriptDiff
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static DiffSummary Summarize(string batchText, string streamText)
    {
        var b = Normalize(batchText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = Normalize(streamText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (b.SequenceEqual(s))
            return new DiffSummary(true, b.Length, s.Length, Array.Empty<string>());

        // LCS-based word diff (transcripts are short; O(n*m) is fine).
        var lcs = new int[b.Length + 1, s.Length + 1];
        for (var i = b.Length - 1; i >= 0; i--)
            for (var j = s.Length - 1; j >= 0; j--)
                lcs[i, j] = b[i] == s[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var diffs = new List<string>();
        int x = 0, y = 0;
        while (x < b.Length && y < s.Length)
        {
            if (b[x] == s[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) diffs.Add("-" + b[x++]);
            else diffs.Add("+" + s[y++]);
        }
        while (x < b.Length) diffs.Add("-" + b[x++]);
        while (y < s.Length) diffs.Add("+" + s[y++]);
        return new DiffSummary(false, b.Length, s.Length, diffs);
    }
}
