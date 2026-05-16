namespace Winpepper.Core.Learning;

/// <summary>
/// Case-insensitive positional edit distance: counts character positions that
/// differ when the two strings are left-aligned, plus the absolute length
/// difference. Equivalent to Hamming distance on the overlapping prefix plus
/// the trailing-tail length, with case ignored.
///
/// Per Plan 5 §8.2 (4): this metric is the budget input for the analyzer's
/// 60 %-of-word-length cap. It is a deliberate simplification of classical
/// two-row Levenshtein — it does not search for an optimal alignment that
/// exploits shared substrings (which would mask real divergence at fixed
/// positions, e.g. case-insensitive Levenshtein collapses "chat gbt" ↔
/// "ChatGPT" to 2 by deleting the space and matching tails, which under-counts
/// the misheard transcription that should hit the cap exactly at 4).
/// </summary>
public static class LevenshteinDistance
{
    public static int Compute(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var overlap = Math.Min(a.Length, b.Length);
        var distance = Math.Abs(a.Length - b.Length);
        for (var i = 0; i < overlap; i++)
        {
            if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]))
                distance++;
        }
        return distance;
    }
}
