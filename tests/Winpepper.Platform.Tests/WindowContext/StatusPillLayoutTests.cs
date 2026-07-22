using Shouldly;
using Winpepper.App.Views;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class StatusPillLayoutTests
{
    [Theory]
    [InlineData(96u, 260, 48, 48, 48)]
    [InlineData(120u, 325, 60, 60, 60)]
    [InlineData(144u, 390, 72, 72, 72)]
    [InlineData(192u, 520, 96, 96, 96)]
    public void ForDpi_ScalesClientAndRoundedRegionTogether(
        uint dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedCornerDiameter,
        int expectedBottomGap)
    {
        var layout = StatusPillLayout.ForDpi(dpi);

        layout.ClientWidth.ShouldBe(expectedWidth);
        layout.ClientHeight.ShouldBe(expectedHeight);
        layout.CornerDiameter.ShouldBe(expectedCornerDiameter);
        layout.BottomGap.ShouldBe(expectedBottomGap);
    }

    [Fact]
    public void ForDpi_RejectsAnInvalidDpi()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StatusPillLayout.ForDpi(0));
    }
}
