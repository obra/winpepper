using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingMelTests
{
    private static float[] RandomAudio(int samples, int seed = 7)
    {
        var rng = new Random(seed);
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = (float)(rng.NextDouble() * 0.8 - 0.4);
        return a;
    }

    private static List<double[]> StreamAll(float[] audio, int chunkSize)
    {
        var extractor = new StreamingLogMelExtractor(PreprocessorConfig.ParakeetTdtV3);
        var frames = new List<double[]>();
        for (var i = 0; i < audio.Length; i += chunkSize)
        {
            extractor.Push(audio.AsSpan(i, Math.Min(chunkSize, audio.Length - i)));
            extractor.Drain(frames);
        }
        extractor.Finish();
        extractor.Drain(frames);
        return frames;
    }

    [Theory]
    [InlineData(1600)]   // 100 ms chunks
    [InlineData(800)]    // 50 ms — the recorder's real cadence
    [InlineData(333)]    // odd size, never aligned to hop
    public void Streaming_MatchesBatch_ExactlyRegardlessOfChunking(int chunkSize)
    {
        var config = PreprocessorConfig.ParakeetTdtV3;
        var audio = RandomAudio(16000 * 2 + 137); // 2 s + odd tail

        var streamed = StreamAll(audio, chunkSize);
        var normalizer = new RunningMelNormalizer(config.FeatureSize);
        normalizer.Add(streamed);
        var streamedNormalized = normalizer.Normalize(streamed);

        var batch = new MelFeatureExtractor(config).Extract(audio);

        streamed.Count.ShouldBe(batch.GetLength(0)); // len/hop + 1 frames
        for (var t = 0; t < streamed.Count; t++)
            for (var m = 0; m < config.FeatureSize; m++)
                ((double)streamedNormalized[t, m]).ShouldBe(batch[t, m], 1e-4,
                    $"frame {t}, mel {m}");
    }

    [Fact]
    public void Streaming_MidStream_OnlyEmitsFramesWithFullRightContext()
    {
        var config = PreprocessorConfig.ParakeetTdtV3;
        var extractor = new StreamingLogMelExtractor(config);
        var frames = new List<double[]>();

        // Frame t needs samples through t*Hop + NFft/2. With exactly NFft/2
        // samples pushed only frame 0 is computable.
        extractor.Push(RandomAudio(config.NFft / 2));
        extractor.Drain(frames);
        frames.Count.ShouldBe(1);
    }

    [Fact]
    public void Drain_IsIncremental_NeverReEmitsFrames()
    {
        var audio = RandomAudio(16000);
        var extractor = new StreamingLogMelExtractor(PreprocessorConfig.ParakeetTdtV3);
        var a = new List<double[]>();
        extractor.Push(audio);
        extractor.Drain(a);
        var countAfterFirstDrain = a.Count;
        extractor.Drain(a);
        a.Count.ShouldBe(countAfterFirstDrain);
    }

    [Fact]
    public void RunningNormalizer_WithAllFramesUpFront_EqualsBatchNormalization()
    {
        // Covered numerically by the exactness theory above; this pins the shape
        // and the ddof=1 divisor for a tiny hand-checkable input.
        var normalizer = new RunningMelNormalizer(featureSize: 1);
        var frames = new List<double[]> { new[] { 1.0 }, new[] { 3.0 } };
        normalizer.Add(frames);
        var norm = normalizer.Normalize(frames);
        // mean 2, ddof=1 variance ((1)^2+(1)^2)/1 = 2, std ~1.41421 + 1e-5
        ((double)norm[0, 0]).ShouldBe(-0.70710, 1e-3);
        ((double)norm[1, 0]).ShouldBe(0.70710, 1e-3);
    }
}
