using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class SilenceTrimmerTests
{
    // 16 kHz mono: 320 samples = 20 ms; 9600 = 600 ms; 19200 = 1200 ms.
    private const int Rate = 16000;
    private const int FrameSamples = 320;

    private static float[] Const(int samples, float amp)
    {
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = amp;
        return a;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var outBuf = new float[total];
        var w = 0;
        foreach (var p in parts) { p.CopyTo(outBuf, w); w += p.Length; }
        return outBuf;
    }

    [Fact]
    public void Trim_LiveMicNobodySpoke_IsSilent()
    {
        // Room tone at 0.002 (below the 0.004 speech gate) over 50 frames.
        var buf = Const(50 * FrameSamples, 0.002f);
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeTrue();
        r.Trimmed.Length.ShouldBe(0);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
    }

    [Fact]
    public void Trim_AllSpeechNoSilence_PassesThroughUnchanged()
    {
        var buf = Const(100 * FrameSamples, 0.3f); // 100 frames of speech
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_EmptyInput_IsNotSilentAndEmpty()
    {
        var r = SilenceTrimmer.Trim(ReadOnlySpan<float>.Empty);
        r.IsSilent.ShouldBeFalse();
        r.Trimmed.Length.ShouldBe(0);
        r.RemovedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_SubFrameBuffer_PassesThroughUnchanged()
    {
        var buf = Const(100, 0.3f); // < 1 frame
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.Trimmed.Length.ShouldBe(100);
    }

    [Fact]
    public void Trim_Interior3sGap_BecomesExactly1200msSplit600_600()
    {
        // 500 ms speech | 3000 ms silence | 500 ms speech
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),   // 8000 samples speech
            Const(150 * FrameSamples, 0.0f),  // 48000 samples silence
            Const(25 * FrameSamples, 0.3f));  // 8000 samples speech

        var r = SilenceTrimmer.Trim(buf);

        r.IsSilent.ShouldBeFalse();
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1800); // 3000 - 1200 removed
        // 8000 speech + 19200 silence (1200 ms) + 8000 speech
        r.Trimmed.Length.ShouldBe(8000 + 19200 + 8000);
        r.Trimmed[7999].ShouldBe(0.3f);  // end of first speech block
        r.Trimmed[8000].ShouldBe(0.0f);  // 600 ms kept after speech
        r.Trimmed[27199].ShouldBe(0.0f); // 600 ms kept before speech
        r.Trimmed[27200].ShouldBe(0.3f); // second speech block resumes
    }

    [Fact]
    public void Trim_InteriorGapExactly1200ms_Untouched()
    {
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(60 * FrameSamples, 0.0f),   // exactly 1200 ms
            Const(25 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_InteriorGap1100ms_Untouched()
    {
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(55 * FrameSamples, 0.0f),   // 1100 ms
            Const(25 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_LeadingLongSilence_Keeps600msAdjacentToSpeech()
    {
        // 2000 ms leading silence | 1000 ms speech
        var buf = Concat(
            Const(100 * FrameSamples, 0.0f),  // 32000 silence
            Const(50 * FrameSamples, 0.3f));  // 16000 speech
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1400);           // 2000 - 600 removed
        r.Trimmed.Length.ShouldBe(9600 + 16000);
        r.Trimmed[9599].ShouldBe(0.0f);       // last kept silence sample
        r.Trimmed[9600].ShouldBe(0.3f);       // speech starts right after 600 ms
    }

    [Fact]
    public void Trim_TrailingLongSilence_Keeps600msAdjacentToSpeech()
    {
        // 1000 ms speech | 2000 ms trailing silence
        var buf = Concat(
            Const(50 * FrameSamples, 0.3f),   // 16000 speech
            Const(100 * FrameSamples, 0.0f)); // 32000 silence
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1400);
        r.Trimmed.Length.ShouldBe(16000 + 9600);
        r.Trimmed[15999].ShouldBe(0.3f);      // end of speech
        r.Trimmed[16000].ShouldBe(0.0f);      // 600 ms of trailing silence kept
    }

    [Fact]
    public void Trim_TailRemainderBeyondLastFrame_IsPreserved()
    {
        // Interior trim + a 7-sample non-frame-aligned tail marked 0.777.
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(150 * FrameSamples, 0.0f),
            Const(25 * FrameSamples, 0.3f),
            Const(7, 0.777f));                // tail: 7 samples, no full frame
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(1800);
        r.Trimmed.Length.ShouldBe(8000 + 19200 + 8000 + 7);
        for (var i = 0; i < 7; i++)
            r.Trimmed[^(i + 1)].ShouldBe(0.777f); // tail survives at the end
    }

    [Fact]
    public void Trim_TwoInteriorGaps_AccountsRemovedMsAndRuns()
    {
        // speech | 2000 ms gap | speech | 2000 ms gap | speech
        var buf = Concat(
            Const(20 * FrameSamples, 0.3f),
            Const(100 * FrameSamples, 0.0f),
            Const(20 * FrameSamples, 0.3f),
            Const(100 * FrameSamples, 0.0f),
            Const(20 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(2);
        r.RemovedMs.ShouldBe(1600); // (2000-1200) removed per interior gap, x2
    }

    [Fact]
    public void Trim_NoisyFloorRelativeToSpeech_IsNoOp()
    {
        // Speech at 0.05, "silence" at 0.01 (high floor vs speech).
        // noiseFloor≈0.01, speechLevel≈0.05 -> 3*floor=0.03 capped at
        // 0.15*0.05=0.0075; silence frames (0.01) stay ABOVE 0.0075 -> not
        // classified as silence -> nothing trimmed.
        var buf = Concat(
            Const(25 * FrameSamples, 0.05f),
            Const(150 * FrameSamples, 0.01f),
            Const(25 * FrameSamples, 0.05f));
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }
}
