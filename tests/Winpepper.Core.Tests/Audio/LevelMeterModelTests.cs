using Shouldly;
using Winpepper.Core.Audio;
using Xunit;

namespace Winpepper.Core.Tests.Audio;

public class LevelMeterModelTests
{
    [Fact]
    public void Peak_ReturnsMaxAbsoluteSample()
    {
        var frame = new float[] { 0.1f, -0.9f, 0.3f };
        LevelMeterModel.Peak(frame).ShouldBe(0.9, 0.0001);
    }

    [Fact]
    public void Peak_ClampsAboveOneToOne()
    {
        var frame = new float[] { 2.0f, -3.0f };
        LevelMeterModel.Peak(frame).ShouldBe(1.0, 0.0001);
    }

    [Fact]
    public void Peak_EmptyFrameIsZero()
    {
        LevelMeterModel.Peak(System.Array.Empty<float>()).ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void Push_RisesTowardPeakUsingAttackCoefficient()
    {
        var m = new LevelMeterModel(attack: 0.5, decay: 0.15);
        // from 0, peak 1.0, attack 0.5 -> 0 + (1-0)*0.5 = 0.5
        m.Push(new float[] { 1.0f }).ShouldBe(0.5, 0.0001);
        // from 0.5, peak 1.0 -> 0.5 + (1-0.5)*0.5 = 0.75
        m.Push(new float[] { 1.0f }).ShouldBe(0.75, 0.0001);
    }

    [Fact]
    public void Push_FallsSlowlyUsingDecayCoefficient()
    {
        var m = new LevelMeterModel(attack: 1.0, decay: 0.15);
        m.Push(new float[] { 1.0f }).ShouldBe(1.0, 0.0001); // attack 1.0 -> jumps to peak
        // silent frame, peak 0, decay 0.15 -> 1.0 + (0-1)*0.15 = 0.85
        m.Push(new float[] { 0.0f }).ShouldBe(0.85, 0.0001);
    }

    [Fact]
    public void Push_StaysWithinZeroToOne()
    {
        var m = new LevelMeterModel();
        for (var i = 0; i < 50; i++)
        {
            var lvl = m.Push(new float[] { 5.0f, -5.0f });
            lvl.ShouldBeInRange(0.0, 1.0);
        }
    }

    [Fact]
    public void Reset_ReturnsLevelToZero()
    {
        var m = new LevelMeterModel(attack: 1.0);
        m.Push(new float[] { 1.0f });
        m.Level.ShouldBe(1.0, 0.0001);
        m.Reset();
        m.Level.ShouldBe(0.0, 0.0001);
    }
}
