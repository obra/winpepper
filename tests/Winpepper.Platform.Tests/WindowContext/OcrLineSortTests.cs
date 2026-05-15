using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class OcrLineSortTests
{
    [Fact]
    public void SortAndJoin_OrdersLinesTopToBottom_WordsLeftToRight()
    {
        var input = new List<OcrLineSort.Line>
        {
            new(Top: 50, Words: new()
            {
                new(Left: 100, Text: "right"),
                new(Left: 10,  Text: "left"),
            }),
            new(Top: 10, Words: new()
            {
                new(Left: 20, Text: "early"),
            }),
        };
        OcrLineSort.SortAndJoin(input).ShouldBe("early\nleft right");
    }

    [Fact]
    public void AverageConfidence_AveragesAcrossAllWords()
    {
        var input = new List<OcrLineSort.Line>
        {
            new(Top: 0, Words: new()
            {
                new(Left: 0, Text: "a", Confidence: 0.9),
                new(Left: 1, Text: "b", Confidence: 0.5),
            }),
            new(Top: 1, Words: new()
            {
                new(Left: 0, Text: "c", Confidence: 0.7),
            }),
        };
        OcrLineSort.AverageConfidence(input).ShouldBe((0.9 + 0.5 + 0.7) / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void AverageConfidence_NoWords_ReturnsZero()
    {
        OcrLineSort.AverageConfidence(new List<OcrLineSort.Line>()).ShouldBe(0.0);
    }

    [Fact]
    public void SortAndJoin_Truncates_At4000Chars()
    {
        var line = new OcrLineSort.Line(Top: 0, Words: new()
        {
            new(Left: 0, Text: new string('x', 5000), Confidence: 1.0),
        });
        OcrLineSort.SortAndJoin(new[] { line }, maxChars: 4000).Length.ShouldBe(4000);
    }
}
