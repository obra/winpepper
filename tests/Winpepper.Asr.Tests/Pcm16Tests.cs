using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class Pcm16Tests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, short.MaxValue)]
    [InlineData(-1f, short.MinValue)]
    [InlineData(2f, short.MaxValue)]    // clamped
    [InlineData(-2f, short.MinValue)]   // clamped
    public void SampleToPcm16_ConvertsAndClamps(float input, short expected)
        => Pcm16.SampleToPcm16(input).ShouldBe(expected);

    [Fact]
    public void FromFloats_ProducesLittleEndianPairs()
    {
        var bytes = Pcm16.FromFloats(new[] { 0f, 1f });
        bytes.ShouldBe(new byte[] { 0x00, 0x00, 0xFF, 0x7F });
    }

    [Fact]
    public void FromFloats_MatchesTheWavEncoderDataSection()
    {
        var samples = new[] { 0.5f, -0.25f, 0.99f };
        var wav = PcmWavEncoder.EncodeMono16k(samples);
        var raw = Pcm16.FromFloats(samples);
        wav.Skip(44).ToArray().ShouldBe(raw); // WAV header is 44 bytes
    }
}
