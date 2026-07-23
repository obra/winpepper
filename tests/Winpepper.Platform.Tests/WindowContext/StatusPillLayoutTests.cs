using Shouldly;
using Winpepper.App.Views;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class StatusPillLayoutTests
{
    [Theory]
    // width = round(300*dpi/96), height = round(48*dpi/96), gap = round(48*dpi/96)
    [InlineData(96u, 300, 48, 48)]
    [InlineData(120u, 375, 60, 60)]
    [InlineData(144u, 450, 72, 72)]
    [InlineData(192u, 600, 96, 96)]
    public void ForDpi_ScalesClientAndBottomGapTogether(
        uint dpi,
        int expectedWidth,
        int expectedHeight,
        int expectedBottomGap)
    {
        var layout = StatusPillLayout.ForDpi(dpi);

        layout.ClientWidth.ShouldBe(expectedWidth);
        layout.ClientHeight.ShouldBe(expectedHeight);
        layout.BottomGap.ShouldBe(expectedBottomGap);
    }

    [Fact]
    public void ForDpi_RejectsAnInvalidDpi()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StatusPillLayout.ForDpi(0));
    }
}
