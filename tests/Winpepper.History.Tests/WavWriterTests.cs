using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class WavWriterTests : IDisposable
{
    private readonly string _dir;
    public WavWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wavwriter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void WriteMono16kInt16_Writes_Valid_RIFF_Header()
    {
        var path = Path.Combine(_dir, "tone.wav");
        var samples = new float[16000]; // 1 second of silence
        WavWriter.WriteMono16kInt16(path, samples);

        var bytes = File.ReadAllBytes(path);
        // 44-byte RIFF/WAVE header + 2 bytes per sample
        bytes.Length.ShouldBe(44 + 16000 * 2);

        // RIFF
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
        // WAVE
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WAVE");
        // fmt
        System.Text.Encoding.ASCII.GetString(bytes, 12, 4).ShouldBe("fmt ");
        // PCM format code = 1
        BitConverter.ToInt16(bytes, 20).ShouldBe((short)1);
        // 1 channel
        BitConverter.ToInt16(bytes, 22).ShouldBe((short)1);
        // 16000 Hz
        BitConverter.ToInt32(bytes, 24).ShouldBe(16000);
        // 16 bits per sample
        BitConverter.ToInt16(bytes, 34).ShouldBe((short)16);
        // data chunk header
        System.Text.Encoding.ASCII.GetString(bytes, 36, 4).ShouldBe("data");
    }

    [Fact]
    public void WriteMono16kInt16_Clamps_OutOfRange_Floats()
    {
        var path = Path.Combine(_dir, "clip.wav");
        var samples = new[] { 2.0f, -2.0f, 0.0f, 0.5f };
        WavWriter.WriteMono16kInt16(path, samples);
        var bytes = File.ReadAllBytes(path);
        // First sample clamped to +1.0 -> int16 32767
        BitConverter.ToInt16(bytes, 44).ShouldBe(short.MaxValue);
        // Second clamped to -1.0 -> int16 -32768
        BitConverter.ToInt16(bytes, 46).ShouldBe(short.MinValue);
        // 0.0 -> 0
        BitConverter.ToInt16(bytes, 48).ShouldBe((short)0);
        // 0.5 -> ~16384
        BitConverter.ToInt16(bytes, 50).ShouldBe((short)16384);
    }

    [Fact]
    public void WriteMono16kInt16_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deep", "f.wav");
        var samples = new float[4];
        WavWriter.WriteMono16kInt16(path, samples);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void ReadMono16kInt16_RoundTrips()
    {
        var path = Path.Combine(_dir, "rt.wav");
        var samples = new[] { 0.0f, 0.25f, -0.5f, 1.0f };
        WavWriter.WriteMono16kInt16(path, samples);
        var loaded = WavWriter.ReadMono16kInt16(path);
        loaded.Length.ShouldBe(4);
        // int16 quantization tolerance
        loaded[0].ShouldBe(0f, 1e-3);
        loaded[1].ShouldBe(0.25f, 1e-3);
        loaded[2].ShouldBe(-0.5f, 1e-3);
        loaded[3].ShouldBe(1.0f, 1e-3);
    }
}
