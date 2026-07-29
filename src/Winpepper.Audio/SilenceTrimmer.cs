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
    /// 0 when the P90 gate fired (the adaptive threshold is derived from a
    /// speech level that does not exist there) and for sub-frame buffers.
    /// Observability only -- lets the drop log say WHY.
    /// </summary>
    public required int VoicedMs { get; init; }

    /// <summary>
    /// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02).
    /// Absolute, so it is reported on BOTH silent paths (0 only for
    /// sub-frame buffers). Together with MaxFrameRms this makes the gate
    /// constants recalibratable from production logs (they were measured
    /// from one 100-recording archive, 2026-07-28, and are provisional).
    /// </summary>
    public required int ClearVoicedMs { get; init; }

    /// <summary>
    /// Loudest 20 ms frame RMS observed (0 for sub-frame buffers).
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

    public static TrimResult Trim(ReadOnlySpan<float> samples)
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

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var speechLevel = Percentile(sorted, SpeechLevelPercentile);

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is derived
            // from a speech level that does not exist), so VoicedMs reports
            // 0. Clear/max fields are absolute and still meaningful -- they
            // keep long-recording transient near-misses diagnosable from the
            // drop log (the gate constants below are recalibrated from
            // these fields).
            var clearMsAtP90 = 0;
            for (var f = 0; f < frameCount; f++)
                if (rms[f] >= ClearSpeechRmsFloor) clearMsAtP90 += FrameMs;
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearMsAtP90,
                MaxFrameRms = sorted[^1],
            };
        }

        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive threshold based on noise floor and speech level.
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech, silence
        // cannot be confidently separated. Capping the threshold at a fraction
        // of speechLevel keeps genuine silence-vs-speech separable and makes
        // low-SNR recordings a no-op instead of eating real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        var isSilence = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
            isSilence[f] = rms[f] < threshold;

        // Minimum-voiced-duration gate (2026-07-28 transient-rejection fix;
        // AND semantics -- the owner-fixed P90 parameters above are not
        // re-derived, and this gate can only make the verdict MORE silent).
        // Voiced frames are never trimmed (only isSilence frames are), so
        // "voiced in the kept post-trim audio" == "voiced in the input" and
        // we can count directly off isSilence[]/rms[] without re-analyzing
        // the output buffer.
        var voicedMs = 0;
        var clearVoicedMs = 0;
        var maxFrameRms = 0.0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] > maxFrameRms) maxFrameRms = rms[f];
            if (isSilence[f]) continue;
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
