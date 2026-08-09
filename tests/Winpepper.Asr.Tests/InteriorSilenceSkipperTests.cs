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
    public void QuietSpeech_UnderTheFloor_SuppressesRunDropping()
    {
        var (skipper, output) = NewSkipper();
        // Quiet-talker regime: one gate-opening frame at 0.003 RMS (the leading
        // gate guarantees at least one frame >= 0.002 opened the stream), then
        // sustained speech at ~0.001 RMS - below the 0.002 fixed floor, so every
        // such frame classifies as "silent". The running max is 0.003 and
        // 0.002 > 0.15 * 0.003, so the speech cap suppresses ALL dropping: the
        // long run of true zeros must survive whole.
        var opener = ConstantFrames(1, 0.003f);
        var quiet = ConstantFrames(5, 0.001f);
        var input = Concat(opener, quiet, Silence(Frame * 10 * KeepFrames), quiet);

        skipper.Push(input);
        skipper.Flush();

        output.ToArray().ShouldBe(input); // sample-identical: nothing dropped
        skipper.SkippedMs.ShouldBe(0);
        skipper.RunsSkipped.ShouldBe(0);
    }

    [Fact]
    public void NormalSpeech_StillDropsLongRuns()
    {
        var (skipper, output) = NewSkipper();
        // 0.02 RMS is modest but above the suppression boundary
        // (0.15 * 0.02 = 0.003 > 0.002), so dropping stays enabled even with
        // the speech cap in place.
        var speech = ConstantFrames(2, 0.02f);
        var run = MarkedSilence(5 * KeepFrames); // 10 frames, budget is 4
        skipper.Push(Concat(speech, run, speech));
        skipper.Flush();

        var expected = Concat(speech, FramesOf(run, 0, 1, 8, 9), speech);
        output.ToArray().ShouldBe(expected);
        skipper.SkippedMs.ShouldBe(3 * KeepMs); // 10 - 4 = 6 frames = 120 ms
        skipper.RunsSkipped.ShouldBe(1);
    }

    /// <summary>Whole analysis frames filled with a constant, so frame RMS equals it.</summary>
    private static float[] ConstantFrames(int frames, float value)
    {
        var a = new float[frames * Frame];
        Array.Fill(a, value);
        return a;
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

}
