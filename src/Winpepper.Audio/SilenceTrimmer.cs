namespace Winpepper.Audio;

/// <summary>Outcome of <see cref="SilenceTrimmer.Trim"/>.</summary>
public readonly struct TrimResult
{
    /// <summary>Samples to send to ASR. Empty when <see cref="IsSilent"/> is true.</summary>
    public required float[] Trimmed { get; init; }

    /// <summary>Total milliseconds of silence removed (0 when nothing was trimmed).</summary>
    public required int RemovedMs { get; init; }

    /// <summary>Number of below-threshold runs that were compressed.</summary>
    public required int RunsTrimmed { get; init; }

    /// <summary>
    /// True when the recording contains no speech (live mic, nobody spoke).
    /// The caller DROPs such a dictation. Distinct from AudioEnergy's dead-mic
    /// detector: this is a voice-presence check over frame-RMS percentiles.
    /// </summary>
    public required bool IsSilent { get; init; }

    /// <summary>
    /// Milliseconds of voiced (above-adaptive-threshold) audio detected.
    /// Cue-budget-deducted count (in-window frames count, then up to the cue
    /// budget of them is subtracted).
    /// 0 when the P90 gate fired (the adaptive threshold is derived from a
    /// speech level that does not exist there) and for sub-frame buffers.
    /// Observability only -- lets the drop log say WHY.
    /// </summary>
    public required int VoicedMs { get; init; }

    /// <summary>
    /// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02).
    /// Cue-budget-deducted count (in-window frames count, then up to the cue
    /// budget of them is subtracted).
    /// Absolute, so it is reported on BOTH silent paths (0 only for
    /// sub-frame buffers). Together with MaxFrameRms this makes the gate
    /// constants recalibratable from production logs (they were measured
    /// from one 100-recording archive, 2026-07-28, and are provisional).
    /// </summary>
    public required int ClearVoicedMs { get; init; }

    /// <summary>
    /// Max frame RMS over the frames AFTER the cue window (recalibration
    /// field; the cue must not inflate it; 0 when the window covers every
    /// frame).
    /// Observability only -- a dropped short utterance is diagnosable from
    /// the log by how close it came to the 0.02 clear tier.
    /// </summary>
    public required double MaxFrameRms { get; init; }

    /// <summary>
    /// ms offset (from buffer t=0) of the first 20 ms frame at/above the
    /// clear-speech floor (0.02) OUTSIDE the cue-pickup window (the band
    /// [prerollMs, maskMs) where the app's own start cue can masquerade as
    /// speech); null when no such frame exists or the input is sub-frame.
    /// Head-loss diagnostic (2026-08-04) — no influence on the gate verdict
    /// or trimming.
    /// </summary>
    public required int? HeadSpeechAtMs { get; init; }

    /// <summary>
    /// True when head speech lands within the first two frames (offset
    /// &lt; 40 ms) — the signature of speech predating the recording window
    /// (the utterance onset was cut off). Null when HeadSpeechAtMs is null.
    /// </summary>
    public bool? HeadClipped => HeadSpeechAtMs is int h ? h < 40 : null;
}

/// <summary>
/// Pure silence trimmer for a FINISHED 16 kHz mono float session buffer.
///
/// Parameters are FIXED by an on-device experiment (45 real archived dictations
/// transcribed with the real parakeet model, original vs trimmed at caps of
/// 300/500/800/1200/2000 ms). At cap=1200 the experiment removed 59.0 s of
/// audio / saved 11.4 s of ASR time across 45 files with only 5 transcripts
/// changed (9 word-edits, ALL cosmetic — capitalization / comma-vs-period).
/// cap=500 caused real word damage ("ligh", "Great"->"Right"); cap=800 injected
/// a disfluency. 1200 ms is the chosen safe point. Do NOT re-derive these.
///
/// Reuses <see cref="AudioEnergy.Rms"/>. Does NOT touch
/// <see cref="AudioEnergy.SilenceRmsThreshold"/> (a different, dead-mic concern).
/// </summary>
public static class SilenceTrimmer
{
    private const int SampleRate = 16000;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320

    /// <summary>Milliseconds of silence kept adjacent to each speech edge.</summary>
    private const int KeepMsPerEdge = 600;
    private const int KeepFramesPerEdge = KeepMsPerEdge / FrameMs; // 30
    // Interior keep budget is 2 * KeepMsPerEdge = 1200 ms (the experiment cap).

    private const double NoiseFloorPercentile = 0.10;
    private const double SpeechLevelPercentile = 0.90;
    private const double ThresholdNoiseMultiplier = 3.0;
    private const double ThresholdAbsFloor = 0.002;
    private const double SpeechCapFactor = 0.15;

    /// <summary>Below this 90th-percentile RMS the recording has no speech.</summary>
    private const double SilentSpeechLevel = 0.004;

    /// <summary>
    /// Tier 1: minimum total duration of voiced (above-adaptive-threshold)
    /// audio a recording must contain to count as speech. Only meaningful
    /// on the normal path -- below SilentSpeechLevel the adaptive
    /// threshold has no speech level to anchor to and VoicedMs is 0.
    /// RECALIBRATED 2026-08-05: the gate was replicated bit-exactly over
    /// the retained 100-recording archive (computed voiced/clear/max-RMS
    /// match the logged drop lines exactly) plus 90 enriched drop lines
    /// from 14 days of logs; 7 drop-archived recordings were human-labeled.
    /// Two quiet real "you have" takes (voiced 360/500 ms, max frame RMS
    /// 0.0093-0.0185, clear@0.02 = 0) were false-rejected by the old
    /// 600 ms floor; 350 admits both (the 360 ms take passes with ZERO
    /// frame margin). All 93 kept real dictations remain
    /// kept (the rule only loosens). KNOWN SACRIFICE: the 2026-07-28
    /// confirmed ~450 ms transient class (-36..-45 dBFS) is no longer
    /// archived -- voiced >= 350 admits such a transient in a SHORT
    /// recording (the P90 gate still covers long recordings unless the
    /// quiet tier fires). Accepted trade-off: one wasted ASR call on an
    /// archived recording vs 4 lost dictations in 2 days -- see
    /// Trim_BriefQuietTransient_ShortRecording_IsKept_AcceptedTradeoff.
    /// </summary>
    private const int MinVoicedDurationMs = 350;

    /// <summary>
    /// Tier 2 level: frames at or above this RMS (~-34 dBFS) are "clearly
    /// speech-loud". MEASURED (2026-07-28, 100-recording archive): every
    /// archived non-speech file has at most ONE 20 ms frame at or above
    /// 0.02, while loud short utterances reach it -- but 17% of real
    /// dictations never do, so this tier is a loud-short-utterance escape
    /// hatch, NOT a speech test. Since 2026-08-05 it is evaluated on BOTH
    /// paths (including P90-silent); quiet short utterances below it are
    /// handled by the quiet tier (QuietSpeechRmsFloor) -- see
    /// Trim_QuietShortUtterance_IsRescued_ByQuietTier.
    /// </summary>
    private const double ClearSpeechRmsFloor = 0.02;

    /// <summary>
    /// Clear-speech-loud audio needed to bypass the duration floor. 100 ms
    /// = 5 frames: the measured worst non-speech file shows 1 frame at or
    /// above 0.02 (5x margin), while the archived loud short utterance
    /// "Great." has EXACTLY 100 ms of clear audio and 9/93 real dictations
    /// sit in the [100, 200) ms clear band -- do not raise this without new
    /// archive measurements (provisional constants; the drop log's
    /// voiced/clear/max-RMS fields exist for recalibration).
    /// </summary>
    private const int MinClearVoicedDurationMs = 100;

    /// <summary>
    /// Tier 3 level: frames at or above this RMS (~-40 dBFS) are plausibly
    /// QUIET speech. MEASURED (2026-08-05 recalibration: gate replicated
    /// bit-exactly over the retained 100-recording archive + 90 enriched
    /// drop lines from 14 days of logs; 7 drop-archived recordings
    /// human-labeled): the two real long-hold false-rejects carried
    /// 460 ms / 280 ms at or above 0.010 on the P90-silent path, while all
    /// 3 labeled non-speech drops have max frame RMS &lt;= 0.0010 with at
    /// most 60 ms of budget-deducted content at or above 0.010 (start-cue
    /// leakage past the 100 ms cue budget; raw in-window cue footprints at
    /// this floor reach 160 ms).
    /// </summary>
    private const double QuietSpeechRmsFloor = 0.010;

    /// <summary>
    /// Tier 3 duration: quiet-speech audio (>= QuietSpeechRmsFloor,
    /// cue-budget-deducted) needed to count as speech. 240 ms = 4x the
    /// measured 60 ms worst-case budget-deducted start-cue leakage (see
    /// QuietSpeechRmsFloor), and sits at or under both measured real
    /// rescues (460/280 ms). Evaluated on BOTH paths, including
    /// P90-silent.
    /// </summary>
    private const int MinQuietSpeechDurationMs = 240;

    /// <summary>
    /// Trim interior/edge silence and decide whether the recording is
    /// silent. <paramref name="maskMs"/> is the head-of-buffer window in
    /// which the app's own start cue can appear
    /// (StartCueGateMask.ComputeMaskMs); <paramref name="cueBudgetMs"/> is
    /// the cue's own deductible worth
    /// (StartCueGateMask.ComputeCueBudgetMs). Frames in the window COUNT
    /// toward every decision statistic and tally, and up to the budget of
    /// in-window frames is then DEDUCTED from the voiced and clear tallies
    /// (cue-budget deduction, 2026-08-03 -- replaces the window EXCLUSION
    /// that dropped prompt short replies). maskMs &lt;= 0 or
    /// cueBudgetMs &lt;= 0 deducts nothing; both default to 0 = pre-mask
    /// behavior, byte-identical. Trimming offsets and the output buffer
    /// are unaffected by mask and budget by construction.
    /// <paramref name="prerollMs"/> is the pre-roll the recorder actually seeded (buffer t=0 sits that far before the hotkey); it locates the cue-pickup band [prerollMs, maskMs) that the head-speech diagnostic must not scan.
    /// </summary>
    public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0, int prerollMs = 0)
    {
        var n = samples.Length;
        var frameCount = n / FrameSamples;
        if (frameCount == 0)
        {
            // Fewer than one full frame: nothing to analyze. Not "silent" —
            // empty/cancel captures are guarded by the caller's length check.
            return new TrimResult
            {
                Trimmed = samples.ToArray(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = false,
                VoicedMs = 0,
                ClearVoicedMs = 0,
                MaxFrameRms = 0,
                HeadSpeechAtMs = null,
            };
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
            rms[f] = AudioEnergy.Rms(samples.Slice(f * FrameSamples, FrameSamples));

        // Start-cue budget DEDUCTION (2026-08-03, replacing the 2026-08-02
        // window EXCLUSION). The exclusion blinded the gate to the first
        // ~1 s of post-hotkey time (buffer t=0 sits the seeded pre-roll
        // BEFORE the hotkey), so a prompt short reply could not reach the
        // 600/100 ms floors and the WHOLE dictation dropped: 4/10 owner
        // dictations on 2026-08-04 (archive WAVs 173b20b3, 525f0643,
        // 003777a1, 4bf32da1 -- all real speech at 820-1180 ms). Now the
        // window's frames COUNT normally and the gate deducts up to
        // cueBudgetMs (the cue's own worth, derived from the measured cue
        // duration -- StartCueGateMask.ComputeCueBudgetMs) of in-window
        // frames from each tally. A beep-only recording's in-window tally
        // IS the cue (measured 120-140 ms clear pickup), so it deducts to
        // below the floors and still drops; prompt real speech keeps its
        // surplus and passes. Ceil on both conversions: a partially
        // covered frame is fully eligible, a partial budget frame deducts
        // whole.
        var maskFrames = maskMs <= 0 ? 0 : Math.Min((maskMs + FrameMs - 1) / FrameMs, frameCount);
        var budgetFrames = cueBudgetMs <= 0 ? 0 : (cueBudgetMs + FrameMs - 1) / FrameMs;

        // Head-speech diagnostic (2026-08-04 head-loss work): first clearly-
        // speech-loud frame OUTSIDE the cue-pickup band. The cue can only be
        // picked up AFTER the hotkey — measured onset preroll+92..144 ms
        // (StartCueGateMask doc) — so the pre-roll head [0, prerollFrames)
        // is scannable and only [prerollFrames, maskFrames) is excluded.
        // maskMs == 0 means no cue was played: scan everything. Diagnostic
        // only — no effect on the verdict, tallies, or trimming below.
        var prerollFrames = maskFrames == 0 ? 0 : Math.Min(Math.Max(prerollMs, 0) / FrameMs, maskFrames);
        int? headSpeechAtMs = null;
        for (var f = 0; f < frameCount; f++)
        {
            if (f >= prerollFrames && f < maskFrames) continue;
            if (rms[f] >= ClearSpeechRmsFloor) { headSpeechAtMs = f * FrameMs; break; }
        }

        // DECISION statistics run over ALL frames again -- the 2026-08-02
        // post-mask stats exclusion is deliberately REMOVED, not kept.
        // Measured why (2026-08-03 archive, see the cue-budget section of
        // docs/plans/2026-07-29-cleanup-asr-contention-evidence.md): with
        // a prompt short reply the post-mask remainder is statistically
        // starved (clip 173b20b3, 1070 ms: 3 frames left for the
        // percentiles), and the P90-silent gate misfires on real speech
        // (clip 4bf32da1: post-mask P90 0.0012 < 0.004 despite 620 ms of
        // deducted voiced audio -- unfixable by any budget while the
        // exclusion stands). The exclusion's anti-cue duty moves to the
        // budget deduction below; MaxFrameRms stays post-window so the cue
        // still cannot inflate the recalibration fields. Side benefit: the
        // decision threshold IS the trim threshold again (one percentile
        // pass over all frames), so trimming is bit-identical by
        // construction.
        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var speechLevel = Percentile(sorted, SpeechLevelPercentile);

        // Post-window max: drop-log recalibration field; the cue window
        // must not inflate it (0 when the window covers every frame).
        var postWindowMax = 0.0;
        for (var f = maskFrames; f < frameCount; f++)
            if (rms[f] > postWindowMax) postWindowMax = rms[f];

        // Absolute-floor tallies (tier 2 clear and tier 3 quiet), computed
        // on BOTH paths and cue-budget-deducted exactly like the voiced
        // tally: count ALL frames at/above each floor, track the in-window
        // share, then deduct up to the budget of in-window frames.
        var clearFrames = 0;
        var clearFramesInWindow = 0;
        var quietFrames = 0;
        var quietFramesInWindow = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] >= ClearSpeechRmsFloor)
            {
                clearFrames++;
                if (f < maskFrames) clearFramesInWindow++;
            }
            if (rms[f] >= QuietSpeechRmsFloor)
            {
                quietFrames++;
                if (f < maskFrames) quietFramesInWindow++;
            }
        }
        var clearVoicedMs = (clearFrames - Math.Min(budgetFrames, clearFramesInWindow)) * FrameMs;
        var quietVoicedMs = (quietFrames - Math.Min(budgetFrames, quietFramesInWindow)) * FrameMs;

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is
            // derived from a speech level that does not exist), so
            // VoicedMs reports 0. The clear count is reported
            // budget-deducted so the cue cannot inflate the recalibration
            // fields (pre-mask logs showed clear = 60-160 ms of pure beep
            // on every silent drop).
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = postWindowMax,
                HeadSpeechAtMs = headSpeechAtMs,
            };
        }

        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive DECISION threshold -- same formula as always, over all
        // frames (identical to the trim threshold below).
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech,
        // silence cannot be confidently separated. Capping the threshold
        // at a fraction of speechLevel keeps genuine silence-vs-speech
        // separable and makes low-SNR recordings a no-op instead of eating
        // real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        // Tier 1 voiced tally (2026-07-28 transient-rejection fix,
        // recalibrated 2026-08-05). Tally ALL frames, tracking the
        // in-window share, then deduct up to the cue budget of the loudest
        // in-window frames. The tallies are frame COUNTS, so "loudest
        // first" reduces to capping the deduction at the in-window share.
        var voicedFrames = 0;
        var voicedFramesInWindow = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] < threshold) continue;
            voicedFrames++;
            if (f < maskFrames) voicedFramesInWindow++;
        }
        var voicedMs = (voicedFrames - Math.Min(budgetFrames, voicedFramesInWindow)) * FrameMs;
        var maxFrameRms = postWindowMax;

        // Three-tier speech verdict (2026-08-05 recalibration): the
        // recording is SPEECH when ANY tier fires.
        if (voicedMs < MinVoicedDurationMs
            && clearVoicedMs < MinClearVoicedDurationMs
            && quietVoicedMs < MinQuietSpeechDurationMs)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = voicedMs,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = maxFrameRms,
                HeadSpeechAtMs = headSpeechAtMs,
            };
        }

        // TRIM threshold == the decision threshold: both are derived over
        // ALL frames now, so the mask/budget can never move trimming
        // offsets or change the output buffer (the walker's sole input is
        // isSilence[]).
        var trimThreshold = threshold;

        var isSilence = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
            isSilence[f] = rms[f] < trimThreshold;

        // Walk contiguous silence runs; build the ordered list of whole-frame
        // segments to KEEP. Interior runs keep 600 ms per speech edge; edge runs
        // keep 600 ms adjacent to their single speech edge; the middle is deleted.
        var kept = new List<(int start, int len)>();
        var removedFrames = 0;
        var runsTrimmed = 0;

        var i = 0;
        while (i < frameCount)
        {
            if (!isSilence[i])
            {
                AppendKeep(kept, i, 1);
                i++;
                continue;
            }

            var runStart = i;
            while (i < frameCount && isSilence[i]) i++;
            var runEnd = i; // exclusive
            var runLen = runEnd - runStart;

            var hasLeftSpeech = runStart > 0;
            var hasRightSpeech = runEnd < frameCount;
            var edges = (hasLeftSpeech ? 1 : 0) + (hasRightSpeech ? 1 : 0);
            var keepBudget = edges * KeepFramesPerEdge;

            if (edges > 0 && runLen > keepBudget)
            {
                if (hasLeftSpeech) AppendKeep(kept, runStart, KeepFramesPerEdge);
                if (hasRightSpeech) AppendKeep(kept, runEnd - KeepFramesPerEdge, KeepFramesPerEdge);
                removedFrames += runLen - keepBudget;
                runsTrimmed++;
            }
            else
            {
                // Short enough to keep whole, or an all-silence buffer with no
                // speech edge (defensive; the IsSilent gate normally catches it).
                AppendKeep(kept, runStart, runLen);
            }
        }

        var keptFrames = 0;
        foreach (var seg in kept) keptFrames += seg.len;
        var tail = n - frameCount * FrameSamples;
        var outBuf = new float[keptFrames * FrameSamples + tail];

        var w = 0;
        foreach (var (start, len) in kept)
        {
            samples.Slice(start * FrameSamples, len * FrameSamples).CopyTo(outBuf.AsSpan(w));
            w += len * FrameSamples;
        }
        if (tail > 0)
            samples.Slice(frameCount * FrameSamples, tail).CopyTo(outBuf.AsSpan(w));

        return new TrimResult
        {
            Trimmed = outBuf,
            RemovedMs = removedFrames * FrameMs,
            RunsTrimmed = runsTrimmed,
            IsSilent = false,
            VoicedMs = voicedMs,
            ClearVoicedMs = clearVoicedMs,
            MaxFrameRms = maxFrameRms,
            HeadSpeechAtMs = headSpeechAtMs,
        };
    }

    private static void AppendKeep(List<(int start, int len)> segs, int start, int len)
    {
        if (len <= 0) return;
        if (segs.Count > 0)
        {
            var last = segs[^1];
            if (last.start + last.len == start)
            {
                segs[^1] = (last.start, last.len + len);
                return;
            }
        }
        segs.Add((start, len));
    }

    private static double Percentile(double[] sortedAsc, double p)
    {
        if (sortedAsc.Length == 0) return 0.0;
        var idx = (int)Math.Floor(p * (sortedAsc.Length - 1));
        if (idx < 0) idx = 0;
        if (idx >= sortedAsc.Length) idx = sortedAsc.Length - 1;
        return sortedAsc[idx];
    }
}
