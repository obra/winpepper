using System.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Pure-logic helpers for the OCR window-context path. Spec §6.1.
/// </summary>
public static class OcrLineSort
{
    public const int DefaultMaxChars = 4000;

    public readonly record struct Word(int Left, string Text, double Confidence = 1.0);

    public sealed record Line(int Top, List<Word> Words);

    public static string SortAndJoin(IEnumerable<Line> lines, int maxChars = DefaultMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Top))
        {
            if (sb.Length > 0)
            {
                if (sb.Length + 1 > maxChars) break;
                sb.Append('\n');
            }
            var sortedWords = line.Words.OrderBy(w => w.Left);
            var first = true;
            foreach (var w in sortedWords)
            {
                var prefix = first ? "" : " ";
                var addition = prefix + w.Text;
                if (sb.Length + addition.Length > maxChars)
                {
                    sb.Append(addition, 0, maxChars - sb.Length);
                    return sb.ToString();
                }
                sb.Append(addition);
                first = false;
            }
        }
        return sb.ToString();
    }

    public static double AverageConfidence(IEnumerable<Line> lines)
    {
        double sum = 0; int count = 0;
        foreach (var line in lines)
            foreach (var w in line.Words) { sum += w.Confidence; count++; }
        return count == 0 ? 0.0 : sum / count;
    }
}
