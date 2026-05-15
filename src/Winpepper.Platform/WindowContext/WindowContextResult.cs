namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Output of a window-context prefetch. Always non-null; <see cref="Empty"/>
/// is used when nothing usable was recovered.
/// </summary>
public sealed record WindowContextResult(
    WindowContextSource Source,
    string Text,
    int CharCount,
    double? AverageOcrConfidence)
{
    public static WindowContextResult Empty { get; } =
        new(WindowContextSource.Empty, "", 0, null);

    public static WindowContextResult FromUia(string text) =>
        new(WindowContextSource.Uia, text, text.Length, null);

    public static WindowContextResult FromOcr(string text, double averageConfidence) =>
        new(WindowContextSource.Ocr, text, text.Length, averageConfidence);
}
