using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextResultTests
{
    [Fact]
    public void Empty_Has_EmptyTextAndZeroChars()
    {
        var r = WindowContextResult.Empty;
        r.Source.ShouldBe(WindowContextSource.Empty);
        r.Text.ShouldBeEmpty();
        r.CharCount.ShouldBe(0);
        r.AverageOcrConfidence.ShouldBeNull();
    }

    [Fact]
    public void FromUia_CountsChars_AndSetsSource()
    {
        var r = WindowContextResult.FromUia("hello");
        r.Source.ShouldBe(WindowContextSource.Uia);
        r.Text.ShouldBe("hello");
        r.CharCount.ShouldBe(5);
        r.AverageOcrConfidence.ShouldBeNull();
    }

    [Fact]
    public void FromOcr_TracksConfidence()
    {
        var r = WindowContextResult.FromOcr("hi there", averageConfidence: 0.84);
        r.Source.ShouldBe(WindowContextSource.Ocr);
        r.Text.ShouldBe("hi there");
        r.CharCount.ShouldBe(8);
        r.AverageOcrConfidence.ShouldBe(0.84);
    }
}
