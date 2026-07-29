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

    // Duration-based wrappers over Const/Concat: Dc(0.015, 460) is exactly
    // 460 ms (23 frames) of audio whose every 20 ms frame has RMS 0.015.
    private static float[] Dc(double rms, int ms) => Const(Rate * ms / 1000, (float)rms);

    private static float[] Join(params float[][] parts) => Concat(parts);

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

    [Fact]
    public void Trim_BriefQuietTransient_ShortRecording_IsSilent()
    {
        // THE bug (2026-07-28 near-miss class): a ~460 ms transient at 0.015
        // RMS (-36.5 dBFS -- cough/mic-bump loudness, below clear speech) in
        // a 2 s otherwise-silent capture. 23 of 100 frames exceed 0.004, so
        // the proportional P90 gate alone says "speech" and the whole silent
        // recording would be transcribed. The absolute voiced-duration gate
        // must drop it: 460 ms voiced < 600 ms, and no frame reaches the
        // 0.02 clear-speech tier.
        var buf = Join(Dc(0.001, 760), Dc(0.015, 460), Dc(0.001, 780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.Trimmed.Length.ShouldBe(0);
        result.RemovedMs.ShouldBe(0);
        result.RunsTrimmed.ShouldBe(0);
        result.VoicedMs.ShouldBe(460);
        result.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_ModerateVoiced_JustUnderFloor_IsSilent()
    {
        // Boundary pin: 580 ms of quiet voiced audio (0.01 RMS, below the
        // 0.02 clear tier) is under the 600 ms floor -> silent.
        var buf = Join(Dc(0.001, 720), Dc(0.01, 580), Dc(0.001, 700));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_ModerateVoiced_AtDurationFloor_IsKept()
    {
        // Boundary pin: exactly 600 ms of quiet voiced audio (0.01 RMS)
        // meets the floor -> kept. Protects soft-spoken dictation.
        var buf = Join(Dc(0.001, 700), Dc(0.01, 600), Dc(0.001, 700));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_ShortLoudUtterance_IsKept()
    {
        // The must-not-eat-loud-speech guard: a 300 ms one-word utterance
        // ("yes") at clear dictation loudness (0.05 RMS) passes via the
        // clear-speech tier (300 ms >= 100 ms at >= 0.02) even though it is
        // under the 600 ms voiced floor. Passes both before and after the fix.
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_VoicedMs_IsReportedOnKeptAudio()
    {
        // Observability field: 300 ms of 0.05 speech classifies as exactly
        // 15 voiced frames (adaptive threshold = max(3*0.001, 0.002) = 0.003,
        // capped at 0.15*0.05 = 0.0075 -> 0.003; room tone 0.001 is silent).
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var result = SilenceTrimmer.Trim(buf);

        result.VoicedMs.ShouldBe(300);
        result.ClearVoicedMs.ShouldBe(300);
        result.MaxFrameRms.ShouldBe(0.05, 0.0005);
    }

    [Fact]
    public void Trim_ClearSpeech_AtClearTierFloor_IsKept()
    {
        // Boundary pin for the clear tier: exactly 100 ms at 0.05 RMS in a
        // short 700 ms capture. P90 = 0.05 (5/35 loud frames, idx 30 lands on
        // the loud block), threshold = 0.003, voiced = 100 < 600,
        // clear = 100 >= 100 -> kept ONLY via the clear tier. The archived
        // "Great." utterance passes at exactly this boundary -- do not raise
        // the tier without new archive data.
        var buf = Join(Dc(0.001, 300), Dc(0.05, 100), Dc(0.001, 300));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.ClearVoicedMs.ShouldBe(100);
    }

    [Fact]
    public void Trim_ClearSpeech_JustUnderClearTier_IsSilent()
    {
        // The boundary's one-frame-under twin: 80 ms at 0.05 inside
        // quiet-voiced padding. P90 = 0.01, threshold = 0.0015 (the
        // 0.15*speechLevel cap binds), voiced = 460 < 600,
        // clear = 80 < 100 -> silent.
        var buf = Join(Dc(0.001, 700), Dc(0.01, 380), Dc(0.05, 80), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_QuietShortUtterance_IsDropped_KnownResidual()
    {
        // Characterization of the MEASURED residual: the two archived
        // "Thank you." dictations (voiced 240/260 ms, max frame RMS
        // 0.013-0.017) sit inside the transient level band and are now
        // dropped. Encoded here as 260 ms @ 0.015 in a 2 s capture:
        // P90 = 0.015 passes (13/100 frames), threshold = 0.00225,
        // voiced = 260 < 600, clear = 0 -> silent via the NEW gate.
        // Non-destructive (archived). Any future change to this verdict must
        // be a visible decision backed by new archive measurements.
        var buf = Join(Dc(0.001, 860), Dc(0.015, 260), Dc(0.001, 880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(260);
    }

    [Fact]
    public void Trim_SustainedQuietTransient_IsKept_KnownResidual()
    {
        // Characterization of the ACCEPTED residual: a sustained quiet
        // transient >= 600 ms (e.g. a 900 ms door rumble at 0.015 RMS)
        // passes the voiced-duration floor -- an energy detector cannot
        // distinguish it from quiet speech. 45 of 150 frames at 0.015 lift
        // P90 to 0.015 (passes) and voiced = 900 >= 600 -> kept. Mitigation:
        // downstream ASR/cleanup handles garbage; the recording is archived.
        var buf = Join(Dc(0.001, 1000), Dc(0.015, 900), Dc(0.001, 1100));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_BriefQuietTransient_LongRecording_IsSilent()
    {
        // Characterization: the same 460 ms transient in a 10 s capture is
        // only ~4.6% of frames, so the P90 gate already drops it today. The
        // new gate must not change that. On this P90-silent path VoicedMs
        // reports 0 (the adaptive threshold is undefined without a speech
        // level) but the absolute fields stay meaningful for recalibration.
        var buf = Join(Dc(0.001, 4760), Dc(0.015, 460), Dc(0.001, 4780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
        result.MaxFrameRms.ShouldBe(0.015, 0.0005);
    }

    [Fact]
    public void Trim_SparseSpeechBurst_LongRecording_IsSilent_KnownResidual()
    {
        // Characterization of the PRE-EXISTING accepted residual (the
        // 2026-07-24 silence-trimming plan asked for this pin and it was
        // never written): a real 300 ms burst in a 10 s mostly-silent
        // recording lands P90 on the room tone, so the P90 gate fires FIRST
        // and the recording is dropped -- recoverable from the archive.
        // ClearVoicedMs = 300 >= 100 here, pinning the AND semantics: the
        // clear tier is an escape hatch WITHIN the new gate, it never
        // overrides a P90-silent verdict. Any future change to this verdict
        // must be a visible decision.
        var buf = Join(Dc(0.001, 4840), Dc(0.05, 300), Dc(0.001, 4860));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.ClearVoicedMs.ShouldBe(300);
    }
}
