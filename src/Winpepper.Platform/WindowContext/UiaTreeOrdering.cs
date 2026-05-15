using System.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Pure-logic helpers for the UIA window-context path. The COM-bound tree walk
/// lives in <c>UiaTreeReader</c>; everything testable lives here.
/// Spec §6.1: top-to-bottom, left-to-right reading order; dedup; 4000-char cap;
/// fall through to OCR when recovered text &lt; 80 chars.
/// </summary>
public static class UiaTreeOrdering
{
    public const int DefaultMaxChars = 4000;
    public const int DefaultMinViableChars = 80;

    public static IEnumerable<UiaExtractedElement> Sort(IEnumerable<UiaExtractedElement> items) =>
        items
            .OrderBy(e => e.BoundingTop)
            .ThenBy(e => e.BoundingLeft);

    public static IEnumerable<UiaExtractedElement> Dedup(IEnumerable<UiaExtractedElement> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in items)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            if (!seen.Add(e.Text)) continue;
            yield return e;
        }
    }

    public static string Join(IEnumerable<UiaExtractedElement> items, int maxChars = DefaultMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var e in items)
        {
            if (sb.Length > 0) sb.Append('\n');
            var remaining = maxChars - sb.Length;
            if (remaining <= 0) break;
            if (e.Text.Length <= remaining) sb.Append(e.Text);
            else { sb.Append(e.Text, 0, remaining); break; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full pipeline: sort → dedup → join (truncated) → enforce min viable length.
    /// Returns null when the recovered text is shorter than <paramref name="minViableChars"/>
    /// — signalling the caller to fall through to OCR.
    /// </summary>
    public static string? Compose(
        IEnumerable<UiaExtractedElement> items,
        int maxChars = DefaultMaxChars,
        int minViableChars = DefaultMinViableChars)
    {
        var text = Join(Dedup(Sort(items)), maxChars);
        return text.Length < minViableChars ? null : text;
    }
}
