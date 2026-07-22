using Shouldly;
using Winpepper.App.Views;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class StatusPillRegionGeometryTests
{
    [Fact]
    public void Compute_FramelessWindow_RegionMatchesClientExactly()
    {
        // Frameless: client origin == window origin, so Left/Top are 0.
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 100, windowTop: 200,
            clientOriginX: 100, clientOriginY: 200,
            clientWidth: 300, clientHeight: 48);

        r.Left.ShouldBe(0);
        r.Top.ShouldBe(0);
        r.Right.ShouldBe(300);   // exclusive == clientWidth, NO +1 overshoot
        r.Bottom.ShouldBe(48);   // exclusive == clientHeight, NO +1 overshoot
        r.CornerDiameter.ShouldBe(48); // min(300,48) -> true capsule ends
    }

    [Fact]
    public void Compute_WithFrameOffset_UsesClientOffsetForLeftTop()
    {
        // Simulate an 8-px left frame and 30-px top frame.
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 0, windowTop: 0,
            clientOriginX: 8, clientOriginY: 30,
            clientWidth: 260, clientHeight: 48);

        r.Left.ShouldBe(8);
        r.Top.ShouldBe(30);
        r.Right.ShouldBe(8 + 260);
        r.Bottom.ShouldBe(30 + 48);
        r.CornerDiameter.ShouldBe(48);
    }

    [Theory]
    // Corner diameter tracks the SHORTER side so the capsule ends stay round.
    [InlineData(300, 48, 48)]
    [InlineData(450, 72, 72)]   // 150% DPI equivalent
    [InlineData(40, 60, 40)]    // taller than wide -> min is width
    public void Compute_CornerDiameterIsMinOfClientSides(
        int clientWidth, int clientHeight, int expectedDiameter)
    {
        var r = StatusPillRegionGeometry.Compute(
            windowLeft: 0, windowTop: 0,
            clientOriginX: 0, clientOriginY: 0,
            clientWidth: clientWidth, clientHeight: clientHeight);

        r.CornerDiameter.ShouldBe(expectedDiameter);
    }
}
