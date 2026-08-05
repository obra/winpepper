namespace Winpepper.Audio;

/// <summary>
/// Composes the warm pre-roll REQUEST for a dictation session: the base
/// <see cref="StartCueGateMask.WarmPrerollMs"/> plus compensation for the
/// hotkey-observation lag (hook timestamp -> pre-roll seed; hotkey events
/// are handled serially behind the previous dictation's stop path). The
/// pre-roll counts back from the DELAYED StartSession, so every ms of lag
/// eats pre-keydown coverage 1:1 (head-loss investigation, M2; annotated-log
/// population n=1340, 2026-07-29 -> 08-04: lag p50=0 ms, p99~37 ms,
/// max=3241 ms, >100 ms in 12 (0.90%); 617 ms lag event, log session
/// c9a80f2b, 2026-08-03 — 617 ms lag + retrigger = 240 ms unrecorded hole).
/// The lag contribution is clamped to <see cref="LagCompensationCapMs"/> so
/// the request never exceeds what the capture ring can serve, and the WHOLE
/// request is bounded so the seed window never reaches back past the
/// previous stop hotkey (plus <see cref="StopCueGuardMs"/> when sounds are
/// on): the ring is NEVER cleared at session boundaries
/// (WarmCaptureBuffer.cs:42-53/:62-75/:77-87), so an unbounded request hands
/// the new session the previous dictation's tail words (already transcribed)
/// and its stop-beep pickup — production retrigger gaps &lt;1000 ms in
/// 38/1333 pairs (2.9%); falsified-and-fixed 2026-08-04, load-bearing
/// validation. The recorder still reports the ACTUAL seeded pre-roll, so the
/// silence-gate mask (StartCueGateMask.ComputeMaskMs) keeps scaling with
/// reality, not with this request.
/// </summary>
public static class PrerollRequest
{
    /// <summary>
    /// Maximum ms of observed hotkey lag the request may add on top of
    /// WarmPrerollMs. Equals ring capacity (2 s, WarmWasapiRecorder.
    /// RingCapacitySamples) minus the 1000 ms base — keep the two in
    /// lockstep when either changes. cap=1000 covers all but 5/1340 surveyed
    /// sessions (0.37%, lag &gt;1000 ms) — and those lags co-occur with
    /// quick retriggers where the previous-stop bound forbids further
    /// reach-back anyway; the residual (worst observed 3241 ms =&gt; up to
    /// ~2.2 s uncompensated) is explicitly accepted.
    /// </summary>
    public const int LagCompensationCapMs = 1000;

    /// <summary>
    /// Worst-case request (fully clamped lag; the previous-stop bound only
    /// ever SHRINKS a request). Feeds the startup worst-case mask
    /// observability line, which must remain a ceiling now that per-session
    /// requests vary with lag.
    /// </summary>
    public const int MaxRequestMs = StartCueGateMask.WarmPrerollMs + LagCompensationCapMs;

    /// <summary>
    /// The window (ms) after a stop hotkey in which the PREVIOUS session's
    /// stop-cue mic pickup can still land in the capture ring:
    /// CueStartLatencyMarginMs 200 + stop cue ~150 ms (stop.wav is
    /// byte-equivalent to start.wav, measured 2026-08-04) +
    /// CueDecayMarginMs 150. Applied only when sounds are enabled.
    /// </summary>
    public const int StopCueGuardMs = 500;

    /// <summary>
    /// The includePrerollMs to pass to IWarmAudioRecorder.StartSession.
    /// Negative lag (clock skew across the hook/handler timestamps)
    /// contributes 0 — never shrink the base request because of it.
    /// msSinceStopHotkey null = no previous stop this process; otherwise the
    /// request is bounded at the previous stop hotkey (+ StopCueGuardMs when
    /// sounds are on). A negative msSinceStopHotkey (clock skew) clamps the
    /// clean span to 0 via Math.Max — the conservative direction.
    /// </summary>
    public static int ComputeRequestMs(int observedLagMs, int? msSinceStopHotkey, bool soundsEnabled)
    {
        var request = StartCueGateMask.WarmPrerollMs + Math.Clamp(observedLagMs, 0, LagCompensationCapMs);
        if (msSinceStopHotkey is int sinceStop)
            request = Math.Min(request, Math.Max(0, sinceStop - (soundsEnabled ? StopCueGuardMs : 0)));
        return request;
    }
}
