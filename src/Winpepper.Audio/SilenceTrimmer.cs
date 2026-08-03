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
    /// Milliseconds of voiced (above-adaptive-threshold) audio detected,
    /// counted over post-cue-mask frames only when a mask is supplied.
    /// 0 when the P90 gate fired (the adaptive threshold is derived from a
    /// speech level that does not exist there) and for sub-frame buffers.
    /// Observability only -- lets the drop log say WHY.
    /// </summary>
    public required int VoicedMs { get; init; }

    /// <summary>
    /// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02),
    /// counted over post-cue-mask frames only when a mask is supplied.
    /// Absolute, so it is reported on BOTH silent paths (0 only for
    /// sub-frame buffers). Together with MaxFrameRms this makes the gate
    /// constants recalibratable from production logs (they were measured
    /// from one 100-recording archive, 2026-07-28, and are provisional).
    /// </summary>
    public required int ClearVoicedMs { get; init; }

    /// <summary>
    /// Loudest 20 ms frame RMS observed outside the cue mask (0 for
    /// sub-frame and fully-masked buffers).
    /// Observability only -- a dropped short utterance is diagnosable from
    /// the log by how close it came to the 0.02 clear tier.
    /// </summary>
    public required double MaxFrameRms { get; init; }
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
    /// Minimum total duration of voiced (above-adaptive-threshold) audio a
    /// recording must contain to count as speech. The P90 gate above is
    /// PROPORTIONAL (needs >10% of frames loud), so a brief non-speech
    /// transient (cough, mic bump, keyboard clatter) in a SHORT recording
    /// can unlock the whole buffer -- confirmed near-miss 2026-07-28
    /// (~450 ms transient at -36..-45 dBFS in an 8.95 s silent recording).
    /// This is an absolute backstop. 600 ms exceeds the confirmed transient
    /// class; real speech shorter than this passes via the clear-speech
    /// tier below. Drops remain non-destructive (original audio archived).
    /// </summary>
    private const int MinVoicedDurationMs = 600;

    /// <summary>
    /// Frames at or above this RMS (~-34 dBFS) are "clearly speech-loud".
    /// MEASURED (2026-07-28, 100-recording archive): every archived
    /// non-speech file has at most ONE 20 ms frame at or above 0.02, while
    /// loud short utterances reach it -- but 17% of real dictations never
    /// do, so this tier is a loud-short-utterance escape hatch, NOT a
    /// speech test. Known residual: quiet short utterances (max frame RMS
    /// 0.013-0.017, e.g. the two archived "Thank you."s) sit inside the
    /// transient level band and are dropped -- see
    /// Trim_QuietShortUtterance_IsDropped_KnownResidual.
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
    /// Trims silence and decides voice-presence for a finished session buffer.
    /// <paramref name="maskMs"/> excludes the leading start-cue window from the
    /// gate DECISION (the P90-silent gate, the decision threshold's P10/P90
    /// percentiles, and the VoicedMs/ClearVoicedMs/MaxFrameRms counting)
    /// WITHOUT touching the trim math: the trim threshold, isSilence[], the
    /// run walker, RemovedMs/RunsTrimmed, and the output buffer are computed
    /// over ALL frames exactly as before, so the transcribed audio and the
    /// trim accounting are mask-independent. maskMs = 0 (the default) is
    /// byte-identical to the pre-mask behavior. The caller computes maskMs
    /// from the actually-seeded pre-roll plus the runtime-measured cue
    /// duration — see <see cref="StartCueGateMask"/>. Partial mask frames round UP
    /// (a mask's job is exclusion). A mask covering every frame classifies
    /// the recording silent (accepted residual: an utterance entirely inside
    /// the mask window is dropped; drops stay non-destructive — the caller
    /// archives the original audio).
    /// </summary>
    public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0)
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
            };
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
            rms[f] = AudioEnergy.Rms(samples.Slice(f * FrameSamples, FrameSamples));

        // Start-cue mask: frames [0, maskFrames) are excluded from every
        // DECISION statistic below but stay in the buffer and in the trim
        // threshold. Ceil, so a partially covered frame is fully excluded.
        var maskFrames = maskMs <= 0 ? 0 : Math.Min((maskMs + FrameMs - 1) / FrameMs, frameCount);
        var decisionFrameCount = frameCount - maskFrames;

        if (decisionFrameCount == 0)
        {
            // Every frame sits inside the cue mask: no decision evidence
            // exists, so the recording is silent by definition. ACCEPTED
            // RESIDUAL: an utterance spoken entirely inside the mask window
            // is dropped (see Trim_UtteranceEntirelyInsideMask_IsSilent_
            // KnownResidual). This branch is also the guard that keeps the
            // percentile math off an empty array (sorted[^1] would throw).
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = 0,
                MaxFrameRms = 0,
            };
        }

        // DECISION statistics run over post-mask frames only. With maskMs = 0
        // this is all frames and everything below matches the pre-mask code.
        var decisionSorted = new double[decisionFrameCount];
        Array.Copy(rms, maskFrames, decisionSorted, 0, decisionFrameCount);
        Array.Sort(decisionSorted);
        var speechLevel = Percentile(decisionSorted, SpeechLevelPercentile);

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is derived
            // from a speech level that does not exist), so VoicedMs reports
            // 0. Clear/max fields are absolute and still meaningful -- they
            // keep long-recording transient near-misses diagnosable from the
            // drop log (the gate constants are recalibrated from these
            // fields). Counted post-mask, so the start cue can no longer
            // inflate the recalibration fields (pre-mask logs showed
            // clear=60-160 ms of pure beep on every silent drop).
            var clearMsAtP90 = 0;
            for (var f = maskFrames; f < frameCount; f++)
                if (rms[f] >= ClearSpeechRmsFloor) clearMsAtP90 += FrameMs;
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearMsAtP90,
                MaxFrameRms = decisionSorted[^1],
            };
        }

        var noiseFloor = Percentile(decisionSorted, NoiseFloorPercentile);

        // Adaptive DECISION threshold based on the post-mask noise floor and
        // speech level (same formula as always; identical to the trim
        // threshold when maskMs = 0).
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech, silence
        // cannot be confidently separated. Capping the threshold at a fraction
        // of speechLevel keeps genuine silence-vs-speech separable and makes
        // low-SNR recordings a no-op instead of eating real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        // Minimum-voiced-duration gate (2026-07-28 transient-rejection fix;
        // AND semantics -- the owner-fixed P90 parameters above are not
        // re-derived, and this gate can only make the verdict MORE silent).
        // Counts post-mask frames only, so the start cue can no longer supply
        // voiced/clear milliseconds (2026-08-02: an unmasked cue alone could
        // satisfy the 100 ms clear tier and unlock a silent recording).
        var voicedMs = 0;
        var clearVoicedMs = 0;
        var maxFrameRms = 0.0;
        for (var f = maskFrames; f < frameCount; f++)
        {
            if (rms[f] > maxFrameRms) maxFrameRms = rms[f];
            if (rms[f] < threshold) continue;
            voicedMs += FrameMs;
            if (rms[f] >= ClearSpeechRmsFloor) clearVoicedMs += FrameMs;
        }

        if (voicedMs < MinVoicedDurationMs && clearVoicedMs < MinClearVoicedDurationMs)
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
            };
        }

        // TRIM threshold: ALL frames, exactly the pre-mask computation, so
        // the mask can never move trimming offsets or change the output
        // buffer. (Masking the percentile sample would shift the threshold
        // and with it isSilence[] -- the walker's sole input.) When
        // maskFrames == 0 the decision threshold IS the all-frames threshold,
        // so the extra sort is skipped.
        var trimThreshold = threshold;
        if (maskFrames > 0)
        {
            var trimSorted = (double[])rms.Clone();
            Array.Sort(trimSorted);
            var trimSpeechLevel = Percentile(trimSorted, SpeechLevelPercentile);
            var trimNoiseFloor = Percentile(trimSorted, NoiseFloorPercentile);
            trimThreshold = Math.Max(ThresholdNoiseMultiplier * trimNoiseFloor, ThresholdAbsFloor);
            trimThreshold = Math.Min(trimThreshold, SpeechCapFactor * trimSpeechLevel);
        }

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
