using System;
using System.Collections.Generic;

namespace AsrLatencyBench;

public sealed record ErrorRate(int Substitutions, int Insertions, int Deletions, int ReferenceLength)
{
    public int Edits => Substitutions + Insertions + Deletions;

    /// <summary>Edits over reference length. Empty reference: 0.0 when the
    /// hypothesis is also empty, else 1.0.</summary>
    public double Rate => ReferenceLength == 0
        ? (Edits == 0 ? 0.0 : 1.0)
        : (double)Edits / ReferenceLength;
}

/// <summary>
/// Word and character error rates against a reference transcript, computed on
/// TranscriptDiff.Normalize output (lowercase, punctuation stripped with
/// apostrophes kept, whitespace collapsed). Deliberately no number-word
/// normalization: digits stay digits for the reference and every candidate
/// model alike, so relative ranking is unaffected. BCL-only so the same file
/// compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalMetrics
{
    public static ErrorRate Wer(string referenceText, string hypothesisText)
        => Align(Tokens(referenceText), Tokens(hypothesisText));

    public static ErrorRate Cer(string referenceText, string hypothesisText)
        => Align(Chars(referenceText), Chars(hypothesisText));

    /// <summary>Expected-silent clips pass when the model produced no words.</summary>
    public static bool SilentPass(string hypothesisText)
        => TranscriptDiff.Normalize(hypothesisText).Length == 0;

    private static string[] Tokens(string text)
        => TranscriptDiff.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static char[] Chars(string text)
        => TranscriptDiff.Normalize(text).Replace(" ", "").ToCharArray();

    private static ErrorRate Align<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis)
        where T : IEquatable<T>
    {
        var n = reference.Count;
        var m = hypothesis.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var sub = d[i - 1, j - 1] + (reference[i - 1].Equals(hypothesis[j - 1]) ? 0 : 1);
                d[i, j] = Math.Min(sub, Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
            }
        }

        int subs = 0, ins = 0, dels = 0, x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && reference[x - 1].Equals(hypothesis[y - 1]) && d[x, y] == d[x - 1, y - 1])
            {
                x--; y--;
            }
            else if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + 1)
            {
                subs++; x--; y--;
            }
            else if (x > 0 && d[x, y] == d[x - 1, y] + 1)
            {
                dels++; x--;
            }
            else
            {
                ins++; y--;
            }
        }
        return new ErrorRate(subs, ins, dels, n);
    }
}
