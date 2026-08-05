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
    public void Trim_BriefQuietTransient_ShortRecording_IsKept_AcceptedTradeoff()
    {
        // KNOWN SACRIFICE of the 2026-08-05 recalibration: the confirmed
        // 2026-07-28 ~450 ms transient class (-36..-45 dBFS) in a SHORT
        // recording now passes via the 350 ms voiced floor (460 >= 350).
        // Accepted trade-off: cost is one wasted ASR call on an archived
        // recording vs 4 real dictations lost in 2 days under the old
        // 600 ms floor (2026-08-05: batch Parakeet transcribed this
        // encoded transient to empty; non-empty hallucinations would
        // reach injection unguarded -- accepted residual). The P90 gate
        // still covers LONG recordings unless the quiet tier fires (see
        // the long-recording pins below).
        var buf = Join(Dc(0.001, 760), Dc(0.015, 460), Dc(0.001, 780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(460);
        result.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_ModerateVoiced_JustUnderFloor_IsSilent()
    {
        // Boundary pin at the 2026-08-05 floor: 340 ms of quiet voiced
        // audio (0.008 RMS -- below the 0.010 quiet tier and the 0.02
        // clear tier) is one frame under the 350 ms floor -> silent.
        // P90 = 0.008 (17/100 frames, idx 89), threshold =
        // min(max(3*0.001, 0.002), 0.15*0.008) = 0.0012.
        var buf = Join(Dc(0.001, 840), Dc(0.008, 340), Dc(0.001, 820));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(340);
    }

    [Fact]
    public void Trim_ModerateVoiced_AtDurationFloor_IsKept()
    {
        // Boundary pin: 360 ms of quiet voiced audio (0.008 RMS) meets the
        // 350 ms floor (first whole frame >= 350) -> kept via tier 1 ALONE
        // (0.008 is below both the 0.010 quiet and 0.02 clear floors).
        // Protects soft-spoken dictation.
        var buf = Join(Dc(0.001, 820), Dc(0.008, 360), Dc(0.001, 820));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(360);
    }

    [Fact]
    public void Trim_QuietShortUtterance_MidVoiced_IsKept_By350Floor()
    {
        // 2026-08-05 recalibration scenario: the two quiet real "you have"
        // takes (voiced 360/500 ms, max frame RMS 0.0093-0.0185,
        // clear@0.02 = 0) were false-rejected by the old 600 ms floor.
        // Encoded as 500 ms @ 0.015 in a 2 s capture: P90 = 0.015
        // (25/100 frames, idx 89), threshold = min(max(3*0.001, 0.002),
        // 0.15*0.015) = 0.00225, voiced = 500 >= 350 -> kept.
        var buf = Join(Dc(0.001, 760), Dc(0.015, 500), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(500);
        result.ClearVoicedMs.ShouldBe(0);
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
        // The clear boundary's one-frame-under twin, recalibrated so no
        // tier fires: clear = 80 < 100; voiced = 180 + 80 = 260 < 350;
        // quiet-tier content (>= 0.010) = 80 < 240 (the 0.008 block sits
        // below the quiet floor). P90 = 0.008 (13/85 frames, idx 75),
        // threshold = min(max(3*0.001, 0.002), 0.15*0.008) = 0.0012.
        var buf = Join(Dc(0.001, 700), Dc(0.008, 180), Dc(0.05, 80), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_QuietShortUtterance_IsRescued_ByQuietTier()
    {
        // Formerly Trim_QuietShortUtterance_IsDropped_KnownResidual -- the
        // 2026-08-05 recalibration flips the verdict. The two logged
        // "Thank you." dictations (drop lines: voiced 240/260 ms,
        // clear@0.02 = 60/80 ms; WAVs since purged, so their quiet@0.010
        // content is unknowable) were REAL SPEECH false-rejects; this
        // fixture encodes the CLASS -- the measured tier-3 anchors are the
        // two archived long-holds (460/280 ms @ >= 0.010). Encoded as 260 ms
        // @ 0.015 in a 2 s capture: P90 = 0.015 passes, voiced = 260 < 350
        // (tier 1 misses), clear@0.02 = 0 (tier 2 misses), but 260 ms
        // >= 0.010 clears the 240 ms quiet tier -> KEPT.
        var buf = Join(Dc(0.001, 860), Dc(0.015, 260), Dc(0.001, 880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
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

    // ------------------------------------------------------------------
    // Start-cue mask (2026-08-03): maskMs marks the head-of-buffer cue window
    // whose frames still COUNT toward the decision statistics and tallies; up to
    // cueBudgetMs of in-window frames is deducted from the voiced/clear tallies
    // (cue-budget deduction, replacing 2026-08-02 window exclusion); trimming
    // offsets and output are unaffected by mask and budget. 1000 ms below =
    // 500 preroll + 200 latency + 150 cue + 150 decay for the shipped asset on
    // a fully-seeded warm session — a representative value, computed in
    // production by StartCueGateMask from the actual seeded pre-roll and the
    // runtime-measured cue.
    // ------------------------------------------------------------------

    [Fact]
    public void Trim_MaskZero_IsIdenticalToUnmasked()
    {
        // Characterization pin: maskMs = 0 must be byte-identical to the
        // one-argument call on a kept, clear-tier recording.
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var r0 = SilenceTrimmer.Trim(buf);
        var rm = SilenceTrimmer.Trim(buf, 0);

        rm.IsSilent.ShouldBe(r0.IsSilent);
        rm.VoicedMs.ShouldBe(r0.VoicedMs);
        rm.ClearVoicedMs.ShouldBe(r0.ClearVoicedMs);
        rm.MaxFrameRms.ShouldBe(r0.MaxFrameRms);
        rm.RemovedMs.ShouldBe(r0.RemovedMs);
        rm.RunsTrimmed.ShouldBe(r0.RunsTrimmed);
        rm.Trimmed.SequenceEqual(r0.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_NegativeMask_TreatedAsZero()
    {
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var r0 = SilenceTrimmer.Trim(buf, 0);
        var rn = SilenceTrimmer.Trim(buf, -100);

        rn.IsSilent.ShouldBe(r0.IsSilent);
        rn.VoicedMs.ShouldBe(r0.VoicedMs);
        rn.ClearVoicedMs.ShouldBe(r0.ClearVoicedMs);
        rn.RemovedMs.ShouldBe(r0.RemovedMs);
        rn.Trimmed.SequenceEqual(r0.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_CueBeepAloneInsideMask_StillDrops_ByBudgetDeduction()
    {
        // THE 2026-08-02 escape class, re-pinned under DEDUCTION semantics.
        // Beep-only recording: the only energy is the cue's mic pickup,
        // modelled at its measured size (140 ms @ 0.05 starting 600 ms in;
        // archive: onset 592-644 ms, clear pickup 120-140 ms of the 150 ms
        // emission). 1200 ms buffer = 60 frames; beep = frames 30-36 (7).
        // Unmasked: P90 idx floor(0.9*59)=53 lands in the 7 loud frames ->
        // P90 0.05 passes the 0.004 gate; thr = min(max(3*0.001, 0.002),
        // 0.15*0.05) = 0.003; voiced 140 < 600 but clear 140 >= 100 -> the
        // escape hatch would PASS a silent recording (pinned below).
        // Masked+budget: window ceil(1000/20)=50 frames, budget
        // ceil(100/20)=5 frames; all 7 beep frames are in-window, so each
        // tally deducts min(5,7)=5 frames: voiced = clear = (7-5)*20 = 40.
        // 40 < 600 && 40 < 100 -> DROPS via the voiced floor.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        SilenceTrimmer.Trim(buf, 0).IsSilent.ShouldBeFalse(); // the escape, pinned

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(40);        // beep residue after deduction
        masked.ClearVoicedMs.ShouldBe(40);   // < 100 ms clear floor
        masked.MaxFrameRms.ShouldBe(0.001, 0.0005); // post-window max, not 0.05
        masked.Trimmed.Length.ShouldBe(0);
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
    }

    [Fact]
    public void Trim_VoicedSpeechAfterMask_StillPasses_TrimmingUnchanged()
    {
        // 2000 ms = 100 frames: 1000 ms room tone | 700 ms speech | 300 ms tone.
        // All-frames decision set = 100 frames: P90 idx floor(0.9*99)=89
        // -> 0.05; threshold 0.003; voiced 700 >= 600 -> kept. Trimming runs
        // on ALL frames: leading 50-frame silence run keeps 30, removes 20
        // (400 ms); trailing 15 <= 30 kept whole.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 700), Dc(0.001, 300));

        var masked = SilenceTrimmer.Trim(buf, 1000);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(700);
        masked.ClearVoicedMs.ShouldBe(700);
        masked.RemovedMs.ShouldBe(400);
        masked.RunsTrimmed.ShouldBe(1);
        masked.Trimmed.Length.ShouldBe(80 * 320); // (100 - 20 removed) frames
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_SpeechStartingInsideMask_KeepsSurplusAfterBudgetDeduction()
    {
        // Speech spans 700-2100 ms -- it STARTS inside the 1000 ms window.
        // 3000 ms = 150 frames: 35 tone | 70 speech (frames 35-104) | 45
        // tone. All-frames stats: P90 idx floor(0.9*149)=134 -> 0.05;
        // thr = min(max(3*0.001, 0.002), 0.0075) = 0.003. Tallies count all
        // 70 speech frames (voiced_all = clear_all = 1400 ms); in-window
        // share = frames 35-49 = 15; deduction = min(budget 5, 15) = 5
        // frames -> voiced = clear = (70-5)*20 = 1300 ms (was 1100 under
        // exclusion: the 300 in-window ms minus the cue's 100 ms worth are
        // the user's own speech, now kept).
        var buf = Join(Dc(0.001, 700), Dc(0.05, 1400), Dc(0.001, 900));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(1300);
        masked.ClearVoicedMs.ShouldBe(1300);
        // Trimming identical to unmasked: leading 35-frame run removes 5
        // (100 ms), trailing 45-frame run removes 15 (300 ms).
        masked.RemovedMs.ShouldBe(400);
        masked.RunsTrimmed.ShouldBe(2);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_MaskDoesNotChangeTrimOffsets_InteriorGap()
    {
        // Trim-invariance headline: an interior-gap shape whose trimming must
        // be bit-identical with and without the mask. 5000 ms = 250 frames:
        // 50 tone | 30 speech | 145 tone | 25 speech. Trimming (all frames):
        // leading run removes 20 (400 ms), interior 145-frame run keeps
        // 2*30 and removes 85 (1700 ms) -> RemovedMs 2100, runs 2.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 600), Dc(0.001, 2900), Dc(0.05, 500));

        var masked = SilenceTrimmer.Trim(buf, 1000);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(1100); // both speech blocks sit after the mask
        masked.RemovedMs.ShouldBe(2100);
        masked.RunsTrimmed.ShouldBe(2);
        unmasked.RemovedMs.ShouldBe(2100);
        unmasked.RunsTrimmed.ShouldBe(2);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_RecordingShorterThanMask_QuietBuffer_IsSilent_DoesNotThrow()
    {
        // 400 ms of near-silence, window 1000 ms: maskFrames clamps to all
        // 20 frames. No special-case branch remains for this -- all-frames
        // P90 = 0.001 < 0.004 -> P90-silent. Nothing is >= 0.02, so the
        // deducted clear count is 0; post-window max over an empty range
        // reports 0.
        var buf = Dc(0.001, 400);

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(0);
        masked.ClearVoicedMs.ShouldBe(0);
        masked.MaxFrameRms.ShouldBe(0.0);
        masked.Trimmed.Length.ShouldBe(0);
    }

    [Fact]
    public void Trim_UtteranceEntirelyInsideMask_NowPassesOnBudgetSurplus()
    {
        // Regression-class recording (2026-08-04, 4/10 owner dictations):
        // a real utterance spoken promptly after the hotkey, ENTIRELY
        // inside the cue window. 1000 ms buffer = 50 frames, speech 500 ms
        // @ 0.05 = frames 15-39 (25 loud). Window covers all 50 frames.
        // All-frames stats: P90 idx floor(0.9*49)=44 -> 0.05; thr = 0.003.
        // voiced_all = clear_all = 500 ms, all in-window; deduction =
        // min(5, 25) = 5 frames -> voiced = clear = (25-5)*20 = 400 ms.
        // clear 400 >= 100 -> PASSES (under exclusion this was the
        // "fully-masked => silent by definition" hard drop).
        var buf = Join(Dc(0.001, 300), Dc(0.05, 500), Dc(0.001, 200));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(400);
        masked.ClearVoicedMs.ShouldBe(400);
        // Window covers every frame -> the post-window observability max
        // is empty by definition and reports 0.
        masked.MaxFrameRms.ShouldBe(0.0);
        // Leading 15-frame and trailing 10-frame silence runs are each
        // <= the 30-frame edge keep -> nothing trimmed.
        masked.RemovedMs.ShouldBe(0);
        masked.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_MaskRoundsUpToWholeFrames_ForDeductionEligibility()
    {
        // One clear frame at 880-900 ms (frame 44) in an otherwise quiet
        // 1200 ms buffer (60 frames). All-frames P90 idx floor(0.9*59)=53
        // -> 0.001 < 0.004: P90-silent path; the reported clear count is
        // budget-deducted. mask 890 -> ceil(890/20) = 45 frames: frame 44
        // is IN the window, so min(budget 5, in-window 1) = 1 frame is
        // deducted -> clear 0. mask 880 -> 44 frames: frame 44 is OUTSIDE,
        // nothing is deduction-eligible -> clear 20. Unmasked reports the
        // raw 20 ms.
        var buf = Join(Dc(0.001, 880), Dc(0.05, 20), Dc(0.001, 300));

        SilenceTrimmer.Trim(buf, 0).ClearVoicedMs.ShouldBe(20);
        SilenceTrimmer.Trim(buf, 880, 100).ClearVoicedMs.ShouldBe(20);
        SilenceTrimmer.Trim(buf, 890, 100).ClearVoicedMs.ShouldBe(0);
        SilenceTrimmer.Trim(buf, 890, 100).IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_CueBudgetRoundsUpToWholeFrames()
    {
        // Beep-only fixture from the escape test: 7 in-window clear frames.
        // budget 90 -> ceil(90/20) = 5 frames deducted, same as budget 100:
        // (7-5)*20 = 40. budget 80 -> exactly 4 frames: (7-4)*20 = 60.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        SilenceTrimmer.Trim(buf, 1000, 90).VoicedMs.ShouldBe(40);
        SilenceTrimmer.Trim(buf, 1000, 80).VoicedMs.ShouldBe(60);
    }

    [Fact]
    public void Trim_BudgetWithoutMask_IsInert()
    {
        // maskMs 0 => no frame is deduction-eligible, so any budget deducts
        // nothing: identical to the plain unmasked call on every field.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        var plain = SilenceTrimmer.Trim(buf, 0);
        var budgeted = SilenceTrimmer.Trim(buf, 0, 100);

        budgeted.IsSilent.ShouldBe(plain.IsSilent);
        budgeted.VoicedMs.ShouldBe(plain.VoicedMs);
        budgeted.ClearVoicedMs.ShouldBe(plain.ClearVoicedMs);
        budgeted.MaxFrameRms.ShouldBe(plain.MaxFrameRms);
        budgeted.RemovedMs.ShouldBe(plain.RemovedMs);
        budgeted.RunsTrimmed.ShouldBe(plain.RunsTrimmed);
        budgeted.Trimmed.SequenceEqual(plain.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_MaskWithZeroBudget_CountsAllFramesWithoutDeduction()
    {
        // Same beep-only fixture: with the window present but budget 0 the
        // tallies include the beep undeducted (voiced = clear = 140) and
        // the clear escape hatch passes -- the 2026-08-02 exclusion is
        // GONE by design; ComputeCueBudgetMs is what closes the escape.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        var r = SilenceTrimmer.Trim(buf, 1000, 0);
        r.IsSilent.ShouldBeFalse();
        r.VoicedMs.ShouldBe(140);
        r.ClearVoicedMs.ShouldBe(140);
    }

    [Fact]
    public void Trim_PromptShortReply_SpeechInsideWindowPastCue_Passes()
    {
        // THE 2026-08-03 regression (4/10 owner dictations dropped): cue
        // pickup at 600-740 ms, the user's short reply at 820-1080 ms --
        // inside the 1000 ms window but past the cue. 1400 ms = 70 frames:
        // cue frames 30-36 (7), speech frames 41-53 (13). Under EXCLUSION
        // the decision saw only frames 50-69: clear 80 < 100, voiced 80 <
        // 600 -> the whole dictation dropped. Under DEDUCTION: all-frames
        // P90 idx floor(0.9*69)=62 -> 0.05; thr = 0.003; voiced_all =
        // clear_all = 20 frames = 400 ms; in-window share = 7+9 = 16
        // frames; deduct min(5,16)=5 -> voiced = clear = 300 >= 100 ->
        // PASSES with the user's surplus intact.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 80),
                       Dc(0.05, 260), Dc(0.001, 320));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(300);
        masked.ClearVoicedMs.ShouldBe(300);
        masked.MaxFrameRms.ShouldBe(0.05, 0.001); // post-window speech frames
        // Leading 30-frame edge run == the 30-frame keep budget, interior
        // 4-frame gap and trailing 16-frame run under their budgets ->
        // nothing trimmed; output identical to unmasked.
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }

    [Fact]
    public void Trim_SpeechAtBufferStart_ReportsHeadSpeechAtZero_AndClipped()
    {
        // Head-loss signature (M1): speech already in progress when the
        // pre-roll ring was seeded. preroll=1000, mask=1500 (1000+200+150+150).
        // Loud frames 0-1 (40 ms @ 0.05) sit in the pre-roll head [0,1000) —
        // OUTSIDE the cue-pickup band [1000,1500) — so they are scannable.
        // Gate: 125 frames, P90 idx floor(0.9*124)=111 -> 0.001 < 0.004 ->
        // P90-silent DROP; head fields must be populated even on the drop path.
        var buf = Join(Dc(0.05, 40), Dc(0.001, 2460));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.IsSilent.ShouldBeTrue();
        r.HeadSpeechAtMs.ShouldBe(0);
        r.HeadClipped.ShouldBe(true);
    }

    [Fact]
    public void Trim_SpeechOnlyInsideCuePickupWindow_OmitsHeadFields()
    {
        // The only clear-tier energy is where the cue lands: frames 50-56
        // (1000-1140 ms), inside the excluded band [1000,1500) at
        // preroll=1000/mask=1500. head_speech_at must NOT report the app's
        // own beep as user speech.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 140), Dc(0.001, 1360));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBeNull();
        r.HeadClipped.ShouldBeNull();
    }

    [Fact]
    public void Trim_SpeechAfterMask_ReportsPostMaskOffset_NotClipped()
    {
        // Speech starts exactly at the mask edge: frames 75-109 (1500-2200 ms,
        // 700 ms @ 0.05). Exclusion [50,75) skipped; first scannable clear
        // frame is 75 -> 1500 ms. Gate passes (voiced 700 >= 600).
        var buf = Join(Dc(0.001, 1500), Dc(0.05, 700), Dc(0.001, 300));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.IsSilent.ShouldBeFalse();
        r.HeadSpeechAtMs.ShouldBe(1500);
        r.HeadClipped.ShouldBe(false);
    }

    [Fact]
    public void Trim_HeadSpeechAt20Ms_IsClipped()
    {
        // Onset in frame 1 (20 ms): still within the first two frames ->
        // clipped. 20 quiet + 800 loud + 780 quiet; preroll=1000/mask=1500;
        // deduction: 40 loud frames in-window minus budget 5 -> 700 ms
        // voiced/clear, passes.
        var buf = Join(Dc(0.001, 20), Dc(0.05, 800), Dc(0.001, 780));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBe(20);
        r.HeadClipped.ShouldBe(true);
    }

    [Fact]
    public void Trim_HeadSpeechAt40Ms_IsNotClipped()
    {
        // Onset in frame 2 (40 ms): first two frames are genuinely quiet, so
        // the utterance onset was captured — not clipped. Pins the < 40 ms
        // boundary (frames 0-1 only).
        var buf = Join(Dc(0.001, 40), Dc(0.05, 800), Dc(0.001, 760));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBe(40);
        r.HeadClipped.ShouldBe(false);
    }

    [Fact]
    public void Trim_NoMask_ScansFromBufferStart()
    {
        // Cue disabled (maskMs=0): nothing was played, nothing to exclude —
        // the scan covers the whole buffer from t=0.
        var buf = Join(Dc(0.05, 700), Dc(0.001, 500));

        var r = SilenceTrimmer.Trim(buf, 0, 0, 0);

        r.HeadSpeechAtMs.ShouldBe(0);
        r.HeadClipped.ShouldBe(true);
    }
}
