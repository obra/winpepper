using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

/// <summary>
/// WavDuration is a HEADER-ONLY, NON-THROWING duration probe for the start-cue
/// asset. Unlike the repo's four existing RIFF readers (WavWriter, PcmWavEncoder's
/// mirror, BenchAudio, the Asr test helpers), it must FAIL OPEN — return false —
/// on anything malformed, because a failed measurement merely disables the
/// silence-gate cue mask (mask 0 = today's behavior). Bytes are synthesized
/// in-test (temp dir + Guid + IDisposable, mirroring WavWriterTests) because
/// checked-in corrupt fixtures would be opaque in review.
/// </summary>
public sealed class WavDurationTests : IDisposable
{
    private readonly string _dir;

    public WavDurationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wavduration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>
    /// Canonical RIFF/WAVE bytes: RIFF + WAVE + optional odd-sized JUNK chunk +
    /// 16-byte fmt + data (zero-filled). Defaults mirror the real shipped
    /// start.wav header: 22050 Hz mono 16-bit PCM, 6616 data bytes = 150 ms.
    /// </summary>
    private static byte[] WavBytes(
        short formatTag = 1,
        short channels = 1,
        int sampleRate = 22050,
        short bitsPerSample = 16,
        int dataBytes = 6616,
        short? blockAlignOverride = null,
        bool junkChunkBeforeFmt = false)
    {
        var blockAlign = blockAlignOverride ?? (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes); // riff size — parser must not trust it
        w.Write("WAVE"u8);
        if (junkChunkBeforeFmt)
        {
            w.Write("JUNK"u8);
            w.Write(3);              // odd size — walker must pad to 4
            w.Write(new byte[4]);    // 3 payload + 1 pad byte
        }
        w.Write("fmt "u8);
        w.Write(16);
        w.Write(formatTag);
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(bitsPerSample);
        w.Write("data"u8);
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void TryMeasureMs_RealStartCueShapedHeader_Returns150()
    {
        // Mirrors the shipped asset exactly: 6616 data bytes / 2 blockAlign
        // = 3308 frames; 3308 * 1000 / 22050 = 150 ms (integer division).
        var path = Write("start-shaped.wav", WavBytes());

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(150);
    }

    [Fact]
    public void TryMeasureMs_16kMonoOneSecond_Returns1000()
    {
        // 32000 bytes / 2 = 16000 frames at 16000 Hz = exactly 1000 ms —
        // same shape as tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.wav.
        var path = Write("one-second.wav", WavBytes(sampleRate: 16000, dataBytes: 32000));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(1000);
    }

    [Fact]
    public void TryMeasureMs_UnknownOddSizedChunkBeforeFmt_IsSkippedWithPadding()
    {
        var path = Write("junk-chunk.wav", WavBytes(junkChunkBeforeFmt: true));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(150);
    }

    [Fact]
    public void TryMeasureMs_ZeroLengthDataChunk_ReturnsTrueZeroMs()
    {
        // A structurally valid but empty cue: parse SUCCEEDS with 0 ms.
        // The caller (StartCueGateMask.ComputeMaskMs) maps duration <= 0 to
        // mask 0, so an inaudible cue never masks anything.
        var path = Write("empty-data.wav", WavBytes(dataBytes: 0));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeTrue();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_MissingFile_ReturnsFalse()
    {
        var path = Path.Combine(_dir, "does-not-exist.wav");

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_ZeroLengthFile_ReturnsFalse()
    {
        var path = Write("zero-bytes.wav", Array.Empty<byte>());

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_GarbageBytes_ReturnsFalse()
    {
        var garbage = new byte[64];
        Array.Fill(garbage, (byte)0xAB);
        var path = Write("garbage.wav", garbage);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_TruncatedDataChunk_ReturnsFalse()
    {
        // Header claims 6616 data bytes but the file is cut 100 bytes into
        // the data chunk. A duration computed from the CLAIMED size would be
        // a lie — fail open instead.
        var whole = WavBytes();
        var truncated = whole[..(44 + 100)];
        var path = Write("truncated-data.wav", truncated);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_TruncatedMidHeader_ReturnsFalse()
    {
        var path = Write("truncated-header.wav", WavBytes()[..20]);

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Theory]
    [InlineData((short)3)]                    // IEEE float
    [InlineData(unchecked((short)0xFFFE))]    // WAVE_FORMAT_EXTENSIBLE
    public void TryMeasureMs_NonPcmFormatTag_ReturnsFalse(short formatTag)
    {
        var path = Write($"non-pcm-{(ushort)formatTag}.wav", WavBytes(formatTag: formatTag));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }

    [Fact]
    public void TryMeasureMs_ZeroBlockAlign_ReturnsFalse()
    {
        // Guards the frames = dataBytes / blockAlign division.
        var path = Write("zero-blockalign.wav", WavBytes(blockAlignOverride: 0));

        WavDuration.TryMeasureMs(path, out var ms).ShouldBeFalse();
        ms.ShouldBe(0);
    }
}
