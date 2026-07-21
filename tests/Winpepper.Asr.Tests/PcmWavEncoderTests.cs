using System.Text;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class PcmWavEncoderTests
{
    [Fact]
    public void Encode_WritesRiffWaveHeader()
    {
        var wav = PcmWavEncoder.EncodeMono16k(new float[] { 0f, 0f, 0f, 0f });
        Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        Encoding.ASCII.GetString(wav, 12, 4).ShouldBe("fmt ");
        Encoding.ASCII.GetString(wav, 36, 4).ShouldBe("data");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // channels
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);          // sample rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);      // bits per sample
    }

    [Fact]
    public void Encode_DataChunkLengthAndSampleConversionAreCorrect()
    {
        var wav = PcmWavEncoder.EncodeMono16k(new float[] { 0f, 1f, -1f });
        // header is 44 bytes; 3 samples * 2 bytes = 6 data bytes
        BitConverter.ToInt32(wav, 40).ShouldBe(6);
        wav.Length.ShouldBe(44 + 6);
        BitConverter.ToInt16(wav, 44).ShouldBe((short)0);          // 0.0  -> 0
        BitConverter.ToInt16(wav, 46).ShouldBe(short.MaxValue);    // +1.0 -> 32767
        BitConverter.ToInt16(wav, 48).ShouldBe(short.MinValue);    // -1.0 -> -32768
    }
}
