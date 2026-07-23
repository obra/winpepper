using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class WindowSizePolicyTests
{
    [Fact]
    public void ComputeDefault_Halves_Height_And_Thirds_Width()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(platformWidth: 1500, platformHeight: 1000);
        w.ShouldBe(500);   // 1500 / 3
        h.ShouldBe(500);   // 1000 / 2
    }

    [Fact]
    public void ComputeDefault_Clamps_To_Minimum_On_Small_Screens()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(platformWidth: 900, platformHeight: 600);
        w.ShouldBe(480);   // 900 / 3 = 300 -> clamped up to 480
        h.ShouldBe(400);   // 600 / 2 = 300 -> clamped up to 400
    }

    [Fact]
    public void ComputeDefault_Respects_Custom_Minimums()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(1200, 800, minWidth: 700, minHeight: 700);
        w.ShouldBe(700);   // 1200 / 3 = 400 -> clamped up to 700
        h.ShouldBe(700);   // 800 / 2 = 400 -> clamped up to 700
    }
}
