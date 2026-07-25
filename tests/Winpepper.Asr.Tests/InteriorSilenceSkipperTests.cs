using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class InteriorSilenceSkipperTests
{
    private const int Frame = 320;  // 20 ms analysis frame at 16 kHz
    private const int KeepMs = 40;  // small keep edge (2 frames) for fast tests
    private const int KeepFrames = KeepMs / 20;

    private static float[] Speech(int samples)
    {
        var rng = new Random(3);
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = (float)(rng.NextDouble() * 0.6 - 0.3);
        return a;
    }

    private static float[] Silence(int samples) => new float[samples];

    /// <summary>
    /// Below the 0.002 RMS floor but frame-distinguishable: analysis frame f is
    /// filled with (f+1)*1e-4 so tests can assert exactly WHICH frames were kept.
    /// </summary>
    private static float[] MarkedSilence(int frames)
    {
        var a = new float[frames * Frame];
        for (var f = 0; f < frames; f++)
            Array.Fill(a, (f + 1) * 1e-4f, f * Frame, Frame);
        return a;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var a = new float[total];
        var w = 0;
        foreach (var p in parts) { p.CopyTo(a, w); w += p.Length; }
        return a;
    }

    private static float[] FramesOf(float[] source, params int[] frames)
        => Concat(frames.Select(f => source.AsSpan(f * Frame, Frame).ToArray()).ToArray());

    private static (InteriorSilenceSkipper Skipper, List<float> Output) NewSkipper()
    {
        var output = new List<float>();
        var skipper = new InteriorSilenceSkipper(m => output.AddRange(m.ToArray()), keepEdgeMs: KeepMs);
        return (skipper, output);
    }

    [Fact]
    public void SpeechOnly_PassesThroughSampleExact()
    {
        var (skipper, output) = NewSkipper();
        var input = Speech(Frame * 10);

        skipper.Push(input);
        skipper.Flush();

        output.ToArray().ShouldBe(input);
        skipper.SkippedMs.ShouldBe(0);
        skipper.RunsSkipped.ShouldBe(0);
    }

    [Fact]
    public void ShortInteriorRun_AtOrBelowTwiceKeepEdge_KeptWhole()
    {
        var (skipper, output) = NewSkipper();
        // Run of exactly 2*keepEdge frames: the interior keep budget, kept whole.
        var input = Concat(Speech(Frame * 3), Silence(Frame * 2 * KeepFrames), Speech(Frame * 3));

        skipper.Push(input);
        skipper.Flush();

        output.ToArray().ShouldBe(input);
        skipper.SkippedMs.ShouldBe(0);
        skipper.RunsSkipped.ShouldBe(0);
    }

    [Fact]
    public void LongInteriorRun_KeepsBothEdges_DropsMiddle()
    {
        var (skipper, output) = NewSkipper();
        var speech = Speech(Frame * 3);
        var run = MarkedSilence(5 * KeepFrames); // 10 frames, budget is 4
        skipper.Push(Concat(speech, run, speech));
        skipper.Flush();

        // Kept: first keepEdge (frames 0,1) + last keepEdge (frames 8,9).
        var expected = Concat(speech, FramesOf(run, 0, 1, 8, 9), speech);
        output.ToArray().ShouldBe(expected);
        skipper.SkippedMs.ShouldBe(3 * KeepMs); // 10 - 4 = 6 frames = 120 ms
        skipper.RunsSkipped.ShouldBe(1);
    }

    [Fact]
    public void LongRun_LeadingEdge_EmittedEagerly_BeforeSpeechResumes()
    {
        var (skipper, output) = NewSkipper();
        var speech = Speech(Frame * 2);
        var run = MarkedSilence(KeepFrames + 1); // outgrows the keep edge, no resume yet
        skipper.Push(Concat(speech, run));

        // The leading keepEdge is kept under every outcome, so it must already be
        // out (bounded buffering) — nothing beyond it, no Flush needed.
        var expected = Concat(speech, FramesOf(run, 0, 1));
        output.ToArray().ShouldBe(expected);
    }

    [Fact]
    public void TrailingSilence_ShortRun_KeptWhole_OnFlush()
    {
        var (skipper, output) = NewSkipper();
        var input = Concat(Speech(Frame * 2), Silence(Frame * KeepFrames));

        skipper.Push(input);
        skipper.Flush();

        output.ToArray().ShouldBe(input);
        skipper.SkippedMs.ShouldBe(0);
        skipper.RunsSkipped.ShouldBe(0);
    }

    [Fact]
    public void TrailingSilence_LongRun_KeepsOnlyLeadingEdge_OnFlush()
    {
        var (skipper, output) = NewSkipper();
        var speech = Speech(Frame * 2);
        var run = MarkedSilence(3 * KeepFrames); // 6 frames, trailing budget is 2
        skipper.Push(Concat(speech, run));
        skipper.Flush();

        var expected = Concat(speech, FramesOf(run, 0, 1));
        output.ToArray().ShouldBe(expected);
        skipper.SkippedMs.ShouldBe(2 * KeepMs); // 6 - 2 = 4 frames = 80 ms
        skipper.RunsSkipped.ShouldBe(1);
    }

    [Fact]
    public void PartialAnalysisFrame_SpansPushes_AndFlushEmitsRemainder()
    {
        var (skipper, output) = NewSkipper();
        var input = Speech(370); // one full frame + a 50-sample partial

        skipper.Push(input.AsMemory(0, 100));
        skipper.Push(input.AsMemory(100, 220));
        skipper.Push(input.AsMemory(320, 50));
        output.Count.ShouldBe(Frame); // the completed frame; the partial is held

        skipper.Flush();

        output.ToArray().ShouldBe(input); // no sample lost or reordered
        skipper.SkippedMs.ShouldBe(0);
    }

    [Fact]
    public void TwoInteriorRuns_EachResolvedIndependently()
    {
        var (skipper, output) = NewSkipper();
        var speech = Speech(Frame * 2);
        var run1 = MarkedSilence(5 * KeepFrames); // 10 frames -> skip 6
        var run2 = MarkedSilence(4 * KeepFrames); // 8 frames -> skip 4
        skipper.Push(Concat(speech, run1, speech, run2, speech));
        skipper.Flush();

        var expected = Concat(
            speech, FramesOf(run1, 0, 1, 8, 9),
            speech, FramesOf(run2, 0, 1, 6, 7),
            speech);
        output.ToArray().ShouldBe(expected);
        skipper.RunsSkipped.ShouldBe(2);
        skipper.SkippedMs.ShouldBe((6 + 4) * 20); // 200 ms across both runs
    }

    [Fact]
    public void GatedStreamingMel_EqualsBatchMel_OverTheKeptConcatenation()
    {
        // Pin the safety claim behind the integration: skipping audio BEFORE the
        // streaming mel extractor is indistinguishable from batch-extracting the
        // shorter kept concatenation. Run the skipper (session defaults) into
        // the streaming extractor while collecting the kept samples, then
        // compare against the batch extractor over that same buffer.
        var config = PreprocessorConfig.ParakeetTdtV3;
        var mel = new StreamingLogMelExtractor(config);
        var kept = new List<float>();
        var skipper = new InteriorSilenceSkipper(m => { kept.AddRange(m.ToArray()); mel.Push(m.Span); });

        // 1 s speech + 2 s silence (> the 1200 ms budget) + speech with an odd
        // tail (exercises the held partial analysis frame at Flush).
        var composite = Concat(Speech(16000), Silence(32000), Speech(8137));
        for (var i = 0; i < composite.Length; i += 800) // the recorder's 50 ms cadence
            skipper.Push(composite.AsMemory(i, Math.Min(800, composite.Length - i)));
        skipper.Flush();
        mel.Finish();
        var frames = new List<double[]>();
        mel.Drain(frames);

        skipper.SkippedMs.ShouldBe(800); // 2000 ms run - 2*600 ms edges
        var normalizer = new RunningMelNormalizer(config.FeatureSize);
        normalizer.Add(frames);
        var streamedNormalized = normalizer.Normalize(frames);
        var batch = new MelFeatureExtractor(config).Extract(kept.ToArray());

        frames.Count.ShouldBe(batch.GetLength(0));
        for (var t = 0; t < frames.Count; t++)
            for (var m = 0; m < config.FeatureSize; m++)
                ((double)streamedNormalized[t, m]).ShouldBe(batch[t, m], 1e-4,
                    $"frame {t}, mel {m}");
    }
}
