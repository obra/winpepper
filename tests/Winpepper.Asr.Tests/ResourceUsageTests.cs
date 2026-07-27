using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ResourceUsageTests
{
    [Fact]
    public void CpuDelta_is_after_minus_before()
    {
        var before = new ResourceSample(1.5, 100);
        var after = new ResourceSample(2.75, 200);
        ResourceUsage.CpuDelta(before, after).ShouldBe(1.25, 1e-9);
    }

    [Fact]
    public void CpuDelta_clamps_negative_to_zero()
    {
        ResourceUsage.CpuDelta(new ResourceSample(5, 0), new ResourceSample(4, 0)).ShouldBe(0);
    }

    [Theory]
    [InlineData(2.0, 10.0, 0.2)]
    [InlineData(10.0, 10.0, 1.0)]
    [InlineData(1.0, 0.0, 0.0)]   // no audio -> 0, never divide by zero
    [InlineData(1.0, -1.0, 0.0)]
    public void Rtf_is_processing_over_audio(double processing, double audio, double expected)
    {
        ResourceUsage.Rtf(processing, audio).ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void ToMb_converts_bytes()
    {
        ResourceUsage.ToMb(3 * 1024 * 1024).ShouldBe(3.0, 1e-9);
    }

    [Fact]
    public void Capture_returns_live_nonnegative_values_and_is_monotonic()
    {
        var a = ResourceUsage.Capture();
        // burn a little CPU so the delta is observable
        var x = 0.0;
        for (var i = 0; i < 5_000_000; i++) x += Math.Sqrt(i);
        x.ShouldBeGreaterThan(0);
        var b = ResourceUsage.Capture();
        a.CpuSeconds.ShouldBeGreaterThanOrEqualTo(0);
        a.PeakWorkingSetBytes.ShouldBeGreaterThan(0);
        b.CpuSeconds.ShouldBeGreaterThanOrEqualTo(a.CpuSeconds);
        b.PeakWorkingSetBytes.ShouldBeGreaterThanOrEqualTo(a.PeakWorkingSetBytes);
    }
}
