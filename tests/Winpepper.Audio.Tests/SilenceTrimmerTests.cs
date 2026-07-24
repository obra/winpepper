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
}
