namespace Winpepper.Platform.Injection;

/// <summary>
/// Deadline-based inter-chunk pacing for the guarded injection send.
/// Measured on the production host (2026-07-28): a SendInput batch of
/// KEYEVENTF_UNICODE down/up events costs ~0.85-1.13 ms PER EVENT, so an
/// 8-code-unit chunk (16 events) costs ~14-18 ms in the SendInput call
/// itself. Sleeping the FULL InterChunkPauseMs after such a send (the old
/// design, which assumed queue-insertion ~us/call) roughly HALVED the real
/// feed (~250-285 units/s against the 571 units/s design point). This pacer
/// sleeps only the REMAINDER of each period: max(0, periodMs - elapsed),
/// where elapsed is measured from the end of the previous pause (so it
/// covers the guard probes AND the send). The remainder is CEILING-rounded,
/// so elapsed + sleep can never undershoot the period -- the bleed-safety
/// ceiling (feed &lt;= TextInjector.TargetFeedUnitsPerSecond) is preserved
/// by construction GIVEN the sleep primitive's error mode is delay:
/// Win32 frames waitable-timer inaccuracy as expiration DELAYS (never-early
/// is not contractual -- stage-2 ledger A1), and the design carries real
/// margin (an 8-unit chunk's 14 ms period exceeds the 13.34 ms the 600
/// ceiling strictly needs by 0.67 ms; a 9-unit chunk's scaled 16 ms exceeds
/// its 15 ms need by 1 ms), absorbing sub-ms jitter. The gate's 5 ms probe
/// pins the high-res timer path; the Thread.Sleep fail-safe is NOT
/// never-early below the ~15.6 ms clock resolution (documented "may sleep
/// less"), which is why a broken timer path is a STOP-and-report gate
/// failure, never a production regime. Chunks larger than the standard
/// 8 units (a surrogate-straddle chunk is 9) must pass their scaled period
/// per call (TextInjector.PeriodMsForChunk; stage-2 ledger A7).
/// If the work alone takes >= the period, no sleep is issued:
/// the feed is then throttled by SendInput itself, inherently at or below
/// the safe rate. Guard cadence is unchanged: the halt predicate still runs
/// once per chunk, i.e. at least once per periodMs-worth of feed.
/// </summary>
internal sealed class DeadlinePacer
{
    private readonly int _periodMs;
    private readonly Action<int> _sleep;
    private readonly Func<double> _monotonicMs;
    private double _periodStartMs;

    /// <param name="periodMs">Minimum per-chunk period (work + sleep).</param>
    /// <param name="sleep">Sleep primitive (production: PacingWaiter.Wait).</param>
    /// <param name="monotonicMs">Monotonic millisecond clock. Period
    /// accounting starts at construction, so construct immediately before
    /// the first chunk is sent.</param>
    public DeadlinePacer(int periodMs, Action<int> sleep, Func<double> monotonicMs)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        _periodMs = periodMs;
        _sleep = sleep;
        _monotonicMs = monotonicMs;
        _periodStartMs = monotonicMs();
    }

    /// <summary>
    /// Sleep the ceiling-rounded remainder of the current period (zero when
    /// the work since the last pause already consumed it), then start the
    /// next period at the end of the sleep. Uses the constructor's default
    /// period (the standard 8-unit chunk).
    /// </summary>
    public void PauseForNextChunk() => PauseForNextChunk(_periodMs);

    /// <summary>
    /// Same, with a per-call period: a chunk larger than the standard
    /// 8 units (InjectionChunker emits 9-unit chunks rather than split a
    /// surrogate pair) needs a proportionally longer period to stay under
    /// the feed ceiling (stage-2 ledger A7).
    /// </summary>
    public void PauseForNextChunk(int periodMs)
    {
        var elapsedMs = _monotonicMs() - _periodStartMs;
        var remainderMs = (int)Math.Ceiling(periodMs - elapsedMs);
        if (remainderMs > 0) _sleep(remainderMs);
        _periodStartMs = _monotonicMs();
    }
}
