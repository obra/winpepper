using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Encodes mono 16 kHz float samples ([-1,+1]) to an in-memory RIFF/WAVE
/// 16-bit PCM blob suitable for AssemblyAI's raw-bytes upload endpoint.
/// Mirrors the on-disk conversion in Winpepper.History.WavWriter.
/// </summary>
public static class PcmWavEncoder
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public static byte[] EncodeMono16k(ReadOnlySpan<float> samples)
    {
        var byteRate = SampleRate * Channels * (BitsPerSample / 8);
        var blockAlign = (short)(Channels * (BitsPerSample / 8));
        var dataBytes = samples.Length * (BitsPerSample / 8);

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);            // PCM fmt chunk size
        w.Write((short)1);      // PCM
        w.Write(Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(BitsPerSample);

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1.0f, 1.0f);
            short pcm;
            if (clamped >= 0f) pcm = (short)Math.Round(clamped * short.MaxValue);
            else pcm = (short)Math.Round(clamped * -(double)short.MinValue);
            w.Write(pcm);
        }

        w.Flush();
        return ms.ToArray();
    }
}
