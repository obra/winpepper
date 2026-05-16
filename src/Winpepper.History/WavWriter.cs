namespace Winpepper.History;

/// <summary>
/// Minimal pure-managed RIFF/WAVE reader and writer for 16 kHz mono int16 PCM.
/// We do this in-project (instead of pulling NAudio into Winpepper.History) so
/// Winpepper.History stays cross-platform and Linux-buildable.
/// </summary>
public static class WavWriter
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public static void WriteMono16kInt16(string path, ReadOnlySpan<float> samples)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var byteRate = SampleRate * Channels * (BitsPerSample / 8);
        var blockAlign = (short)(Channels * (BitsPerSample / 8));
        var dataBytes = samples.Length * (BitsPerSample / 8);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);

        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); // subchunk size for PCM
        w.Write((short)1); // PCM
        w.Write(Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(BitsPerSample);

        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1.0f, 1.0f);
            // -1.0 maps to short.MinValue (-32768) and +1.0 maps to short.MaxValue (+32767).
            short pcm;
            if (clamped >= 0f) pcm = (short)Math.Round(clamped * short.MaxValue);
            else               pcm = (short)Math.Round(clamped * -(double)short.MinValue);
            w.Write(pcm);
        }
    }

    /// <summary>Read a 16 kHz mono int16 WAV back to float samples in [-1, +1].</summary>
    public static float[] ReadMono16kInt16(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs);

        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        r.ReadInt32(); // file size minus 8
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        short bitsPerSample = 0;
        short channels = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (fs.Position < fs.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(r.ReadBytes(4));
            var chunkSize = r.ReadInt32();
            switch (chunkId)
            {
                case "fmt ":
                    var fmtStart = fs.Position;
                    r.ReadInt16(); // pcm format code
                    channels = r.ReadInt16();
                    sampleRate = r.ReadInt32();
                    r.ReadInt32(); // byte rate
                    r.ReadInt16(); // block align
                    bitsPerSample = r.ReadInt16();
                    fs.Position = fmtStart + chunkSize;
                    break;
                case "data":
                    data = r.ReadBytes(chunkSize);
                    break;
                default:
                    fs.Position += chunkSize;
                    break;
            }
        }

        if (channels != 1 || sampleRate != SampleRate || bitsPerSample != 16 || data is null)
            throw new InvalidDataException($"Unexpected WAV format channels={channels} rate={sampleRate} bps={bitsPerSample}");

        var sampleCount = data.Length / 2;
        var result = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var pcm = BitConverter.ToInt16(data, i * 2);
            result[i] = pcm < 0 ? pcm / (float)-(double)short.MinValue : pcm / (float)short.MaxValue;
        }
        return result;
    }
}
