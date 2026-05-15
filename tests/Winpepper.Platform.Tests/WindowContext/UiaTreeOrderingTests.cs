using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class UiaTreeOrderingTests
{
    private static UiaExtractedElement E(string text, int x, int y) =>
        new(text, BoundingLeft: x, BoundingTop: y);

    [Fact]
    public void Sort_ProducesTopToBottom_LeftToRight()
    {
        var items = new List<UiaExtractedElement>
        {
            E("c-right", 200, 100),
            E("a-top",   50,  10),
            E("c-left",  50,  100),
            E("b-mid",   50,  50),
        };
        var ordered = UiaTreeOrdering.Sort(items).Select(e => e.Text).ToList();
        ordered.ShouldBe(new[] { "a-top", "b-mid", "c-left", "c-right" });
    }

    [Fact]
    public void Dedup_RemovesExactDuplicateText_KeepsFirstOccurrence()
    {
        var items = new List<UiaExtractedElement>
        {
            E("hello", 0, 0),
            E("world", 10, 0),
            E("hello", 100, 0), // duplicate text — drop
        };
        var deduped = UiaTreeOrdering.Dedup(items).Select(e => e.Text).ToList();
        deduped.ShouldBe(new[] { "hello", "world" });
    }

    [Fact]
    public void Dedup_TreatsWhitespaceOnlyAsDroppable()
    {
        var items = new List<UiaExtractedElement>
        {
            E("",      0, 0),
            E("  \t",  10, 0),
            E("hello", 20, 0),
        };
        var deduped = UiaTreeOrdering.Dedup(items).Select(e => e.Text).ToList();
        deduped.ShouldBe(new[] { "hello" });
    }

    [Fact]
    public void Join_ConcatenatesWithNewlines_AndTruncatesTo4000()
    {
        var items = new List<UiaExtractedElement>
        {
            E(new string('a', 2000), 0, 0),
            E(new string('b', 2500), 0, 10),
        };
        var text = UiaTreeOrdering.Join(items, maxChars: 4000);
        text.Length.ShouldBe(4000);
        text[..2000].ShouldBe(new string('a', 2000));
    }

    [Fact]
    public void Compose_ShortText_ReturnsEmpty_Per80CharThreshold()
    {
        var items = new List<UiaExtractedElement> { E("hi there", 0, 0) };
        UiaTreeOrdering.Compose(items, maxChars: 4000, minViableChars: 80).ShouldBeNull();
    }

    [Fact]
    public void Compose_LongEnoughText_ReturnsIt()
    {
        var items = new List<UiaExtractedElement>
        {
            E(new string('x', 200), 0, 0),
        };
        var result = UiaTreeOrdering.Compose(items, maxChars: 4000, minViableChars: 80);
        result.ShouldNotBeNull();
        result!.Length.ShouldBe(200);
    }
}
