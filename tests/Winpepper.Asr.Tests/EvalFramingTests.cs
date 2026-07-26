using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalFramingTests
{
    [Fact]
    public void Segments_ClipShorterThanPreroll_IsOneBurst()
    {
        EvalFraming.Segments(5000).ShouldBe(new[] { (0, 5000) });
    }

    [Fact]
    public void Segments_ExactPrerollLength_IsOneBurst()
    {
        EvalFraming.Segments(8000).ShouldBe(new[] { (0, 8000) });
    }

    [Fact]
    public void Segments_LongClip_PrerollBurstThenSteady50msFramesWithRemainder()
    {
        var segs = EvalFraming.Segments(10000);

        segs.ShouldBe(new[] { (0, 8000), (8000, 800), (8800, 800), (9600, 400) });
    }

    [Fact]
    public void Segments_CoverEverySampleExactlyOnce()
    {
        var segs = EvalFraming.Segments(48123);

        segs[0].ShouldBe((0, 8000));
        var covered = 0;
        foreach (var (offset, length) in segs)
        {
            offset.ShouldBe(covered);
            covered += length;
        }
        covered.ShouldBe(48123);
    }

    [Fact]
    public void Segments_ZeroSamples_IsEmpty()
    {
        EvalFraming.Segments(0).ShouldBeEmpty();
    }
}
