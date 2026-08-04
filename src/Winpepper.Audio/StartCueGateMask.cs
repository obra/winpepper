namespace Winpepper.Audio;

/// <summary>
/// Computes (a) the head-of-buffer window in which the app's own start cue
/// can contaminate the mic capture and (b) the cue-budget the silence gate
/// deducts from that window's voiced/clear tallies (cue-budget DEDUCTION,
/// 2026-08-03, replacing the window EXCLUSION that regressed prompt short
/// replies) (validated 2026-08-02/03 over the frozen 100-recording
/// archive: the 150 ms cue is picked up by the mic at ~592-861 ms into every
/// fully-seeded warm buffer at frame RMS up to 0.05 — above the 0.02
/// clear-speech tier — because recording starts with a retroactive warm
/// pre-roll, so buffer t=0 is ~<see cref="WarmPrerollMs"/> before the hotkey
/// and the cue becomes audible 92-144 ms after it).
///
/// Window = actual seeded pre-roll + CueStartLatencyMarginMs + measured cue
/// duration + CueDecayMarginMs. The cue duration is measured at runtime from
/// the shipped WAV (<see cref="WavDuration"/>) — NEVER hardcoded (owner
/// requirement: the asset may change or become user-configurable). The
/// pre-roll is what the recorder ACTUALLY seeded this session
/// (IWarmAudioRecorder.StartSession's return), not the requested worst case:
/// a shorter/zero pre-roll (prewarm off, drained ring) moves the cue EARLIER
/// and the window shrinks with it. Cold-mode validation: a fixed worst-case
/// window flips 4/91 real dictations at 1000 ms; the preroll-aware window
/// flips 0/91.
///
/// Sizing evidence (exact plan semantics over the archive): with the current
/// 150 ms asset the warm window is 1000 ms; 0/91 real gate-passing
/// dictations flip pass->drop, 0/6 true-silent drops flip drop->pass, the
/// confirmed beep-only escape correctly flips to drop, and the tightest
/// passer keeps a 140 ms margin.
/// </summary>
public static class StartCueGateMask
{
    /// <summary>
    /// Warm pre-roll the pipeline REQUESTS at session start. THE single
    /// source of this number: PipelineHost passes it to
    /// StartSession(includePrerollMs:) at both hotkey arms — do not
    /// duplicate the literal. The mask itself is built from the ACTUAL
    /// seeded pre-roll StartSession returns (&lt;= this request).
    /// </summary>
    public const int WarmPrerollMs = 500;

    /// <summary>
    /// Dispatch + render latency between PlayStart() returning and the cue
    /// being audible (SoundPlayer.Play is async fire-and-forget). Measured
    /// over the frozen archive (98 recordings, two independent detectors):
    /// min 92 / p50 115 / p90 131 / max 144 ms; cpu-pegged starts (N=3) at
    /// 130-144 ms. 200 ms leaves 56 ms of headroom over the observed max
    /// (an earlier single ~20 ms observation was falsified — 0/98 within
    /// 50 ms).
    /// </summary>
    public const int CueStartLatencyMarginMs = 200;

    /// <summary>
    /// Room decay/reverb + capture smearing after the cue's emission ends:
    /// measured pickup tail beyond the 150 ms emission is at most 81 ms
    /// (22 overlap-free recordings); pickup ends by 861 ms into the warm
    /// buffer. 150 ms leaves 69 ms of headroom over the observed max. NOTE:
    /// this margin VALUE coincides with the current asset's 150 ms length —
    /// it is a margin constant, not a hardcoded cue duration (the cue
    /// length is always the measured cueMs argument).
    /// </summary>
    public const int CueDecayMarginMs = 150;

    /// <summary>
    /// The window within which SilenceTrimmer's tallies are cue-budget-deductible
    /// (it no longer excludes these frames).
    /// <paramref name="actualPrerollMs"/> is the pre-roll the recorder
    /// ACTUALLY seeded this session (StartSession's return; 0 in cold mode,
    /// negative clamps to 0), so the window shrinks when less pre-hotkey
    /// audio exists. 0 when the cue is disabled (nothing played ⇒ nothing
    /// to mask) or its duration could not be measured (FAIL OPEN: gate
    /// behaves as before the mask existed).
    /// </summary>
    public static int ComputeMaskMs(int actualPrerollMs, int cueMs, bool soundsEnabled)
    {
        if (!soundsEnabled || cueMs <= 0) return 0;
        return Math.Max(actualPrerollMs, 0) + CueStartLatencyMarginMs + cueMs + CueDecayMarginMs;
    }

    /// <summary>
    /// Deliberate under-deduction margin for <see cref="ComputeCueBudgetMs"/>:
    /// the budget deducts this many ms LESS than the measured cue emission.
    /// The cue's clear-tier (>= 0.02 RMS) mic pickup is 120-140 ms of the
    /// 150 ms emission (2026-08-03 archive measurement), so a 50 ms
    /// under-deduction leaves a &lt;= 40 ms beep residue in a beep-only
    /// tally -- safely under the 100 ms clear floor -- while every ms NOT
    /// deducted is a ms of the user's prompt speech preserved. Archive
    /// sweep (two frozen 100-clip corpora, budgets 0-400 ms): the window
    /// satisfying all regression/escape criteria is 100-120 ms; cueMs - 50
    /// = 100 sits at maximum regression margin. NOTE: the 50 ms margin is
    /// validated ONLY at the 150 ms shipped asset; if the cue asset
    /// materially changes, re-run the archive sweep before trusting the
    /// derived budget. Evidence:
    /// docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
    /// (cue-budget deduction section).
    /// </summary>
    public const int CueBudgetMarginMs = 50;

    /// <summary>
    /// The cue's own deductible worth: how many ms of in-window voiced and
    /// clear tally SilenceTrimmer may subtract as "that was probably the
    /// cue, not the user". Derived from the runtime-MEASURED cue duration
    /// (never hardcoded -- the asset may change), 0 when the cue is
    /// disabled or unmeasured (FAIL OPEN: nothing was played, nothing is
    /// deducted, the gate behaves as before the mask existed). NOTE the
    /// asymmetry with <see cref="ComputeMaskMs"/>: the mask WINDOW is
    /// sized generously (over-masking a window that merely marks frames as
    /// deduction-eligible is safe), but the BUDGET is sized tightly --
    /// over-deducting eats the user's own prompt speech, which is exactly
    /// the 2026-08-03 regression this replaces.
    /// </summary>
    public static int ComputeCueBudgetMs(int cueMs, bool soundsEnabled)
    {
        if (!soundsEnabled || cueMs <= 0) return 0;
        return Math.Max(cueMs - CueBudgetMarginMs, 0);
    }
}
