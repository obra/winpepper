using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class AudioEnergyTests
{
    [Fact]
    public void Rms_OfAllZeros_IsZero()
    {
        AudioEnergy.Rms(new float[512]).ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Rms_OfConstantAmplitude_EqualsThatAmplitude()
    {
        var frame = new float[1000];
        for (var i = 0; i < frame.Length; i++) frame[i] = 0.5f;
        AudioEnergy.Rms(frame).ShouldBe(0.5, 1e-6);
    }

    [Fact]
    public void IsSessionSilent_TrueForZeroFilledSession()
    {
        AudioEnergy.IsSessionSilent(new float[16000]).ShouldBeTrue();
    }

    [Fact]
    public void IsSessionSilent_TrueForNearZeroBelowThreshold()
    {
        var frame = new float[16000];
        for (var i = 0; i < frame.Length; i++) frame[i] = 5e-5f; // rms < 1e-4
        AudioEnergy.IsSessionSilent(frame).ShouldBeTrue();
    }

    [Fact]
    public void IsSessionSilent_FalseForRealSpeechLevel()
    {
        var frame = new float[16000];
        for (var i = 0; i < frame.Length; i++) frame[i] = (i % 2 == 0) ? 0.2f : -0.2f;
        AudioEnergy.IsSessionSilent(frame).ShouldBeFalse();
    }

    [Fact]
    public void IsSessionSilent_FalseForEmptySession()
    {
        // Nothing captured is handled by the caller's length guard, not here.
        AudioEnergy.IsSessionSilent(ReadOnlySpan<float>.Empty).ShouldBeFalse();
    }
}
