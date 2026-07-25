namespace Winpepper.Asr.Transcription;

/// <summary>Float [-1,+1] → PCM16LE conversion shared by the WAV encoder (batch
/// upload) and the streaming WebSocket (raw binary frames).</summary>
public static class Pcm16
{
    public static short SampleToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1.0f, 1.0f);
        return clamped >= 0f
            ? (short)Math.Round(clamped * short.MaxValue)
            : (short)Math.Round(clamped * -(double)short.MinValue);
    }

    public static byte[] FromFloats(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var pcm = SampleToPcm16(samples[i]);
            bytes[i * 2] = (byte)(pcm & 0xFF);
            bytes[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }
        return bytes;
    }
}
