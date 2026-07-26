using System;
using System.IO;
using System.Linq;
using AsrLatencyBench;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class BenchHelpersTests
{
    private static string WriteTempWav(float[] samples)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, PcmWavEncoder.EncodeMono16k(samples).ToArray());
        return path;
    }

    [Fact]
    public void ReadMono16k_RoundTrips_PcmWavEncoderOutput()
    {
        var samples = Enumerable.Range(0, 1600)
            .Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 0.5f)
            .ToArray();
        var path = WriteTempWav(samples);
        try
        {
            var read = BenchAudio.ReadMono16k(path);
            Assert.Equal(samples.Length, read.Length);
            for (var i = 0; i < samples.Length; i++)
                Assert.True(Math.Abs(samples[i] - read[i]) < 0.001f, $"sample {i}: {samples[i]} vs {read[i]}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadMono16k_Rejects_WrongSampleRate()
    {
        var bytes = PcmWavEncoder.EncodeMono16k(new float[1600]).ToArray();
        BitConverter.GetBytes(8000).CopyTo(bytes, 24); // canonical fmt-chunk sample-rate offset
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Throws<InvalidDataException>(() => BenchAudio.ReadMono16k(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyGain_Scales_And_Clamps()
    {
        var result = BenchAudio.ApplyGain(new[] { 0.5f, -0.5f, 0.9f }, 2.0);
        Assert.Equal(1.0f, result[0]);      // 1.0 after clamp
        Assert.Equal(-1.0f, result[1]);     // -1.0 after clamp
        Assert.Equal(1.0f, result[2]);      // clamped
        var quiet = BenchAudio.ApplyGain(new[] { 0.5f }, 0.1);
        Assert.True(Math.Abs(quiet[0] - 0.05f) < 1e-6f);
    }

    [Fact]
    public void PrependSilence_Adds_LeadingZeros()
    {
        var result = BenchAudio.PrependSilence(new[] { 0.3f, 0.4f }, 100);
        Assert.Equal(1600 + 2, result.Length); // 100 ms @ 16 kHz = 1600 samples
        Assert.All(result.Take(1600), s => Assert.Equal(0f, s));
        Assert.Equal(0.3f, result[1600]);
        Assert.Equal(0.4f, result[1601]);
    }

    [Fact]
    public void Stats_Reports_Rms_Peak_And_MaxFrameRms()
    {
        // 320 loud samples followed by 320 silent samples.
        var samples = Enumerable.Repeat(0.5f, 320).Concat(Enumerable.Repeat(0f, 320)).ToArray();
        var (rms, peak, maxFrameRms) = BenchAudio.Stats(samples);
        Assert.Equal(0.5, peak, 3);
        Assert.Equal(0.5, maxFrameRms, 3);                    // the loud frame
        Assert.Equal(0.5 / Math.Sqrt(2), rms, 3);             // half the energy overall
    }

    [Fact]
    public void Normalize_Strips_Case_Punctuation_And_Whitespace()
    {
        Assert.Equal("hello world", TranscriptDiff.Normalize("  Hello,   World! "));
        Assert.Equal("don't stop", TranscriptDiff.Normalize("Don't stop."));
    }

    [Fact]
    public void Summarize_TrivialOnly_When_Only_Punctuation_Differs()
    {
        var diff = TranscriptDiff.Summarize("Send the report, please.", "send the report please");
        Assert.True(diff.TrivialOnly);
        Assert.Empty(diff.WordDiffs);
        Assert.Equal(4, diff.BatchWordCount);
    }

    [Fact]
    public void Summarize_Lists_WordLevel_Diffs()
    {
        var diff = TranscriptDiff.Summarize("send the report", "send that report");
        Assert.False(diff.TrivialOnly);
        Assert.Contains("-the", diff.WordDiffs);
        Assert.Contains("+that", diff.WordDiffs);
        Assert.Equal(3, diff.BatchWordCount);
        Assert.Equal(3, diff.StreamWordCount);
    }
}
