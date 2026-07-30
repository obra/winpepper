namespace Winpepper.Asr.Transcription;

/// <summary>E2: the drain's early-abandon decision, pure so it is
/// Linux-testable. A wedged native call cannot be aborted; when the CURRENT
/// in-flight call has already been running at least as long as the FULL
/// drain budget, waiting the budget out buys nothing — the pump cannot
/// possibly drain in time. Abandon immediately so the caller's late batch
/// path starts up to a full deadline sooner (observed pre-fix: asr_wait
/// pegged at 10013-10024 ms on every wedged batch fallback; 13-18 s total
/// per dictation; the saving is an upper bound — contention stretch of the
/// concurrent batch unmeasured, field-bounded &lt;= ~2x).
/// Semantics: abandon iff inFlightElapsed >= max(effectiveDeadline,
/// MinInFlightForFutility). `elapsed >= effectiveDeadline` alone is NOT a
/// futility proof — it bounds the PAST, not the REMAINING time — and the
/// zero-push shortcut shrinks the effective deadline to ~1.5 s, where
/// healthy 2.9-3.96 s calls (observed and recovered in the field) would be
/// wrongly abandoned AND arm E1's blockade off a non-wedge.</summary>
public static class DrainAbandonPolicy
{
    /// <summary>Futility floor == the full 10 s drain budget: >= the 5 s
    /// compute-gate timeout + the observed healthy native max (~4 s), while
    /// real wedges run >= 15 s. Since every effective deadline is &lt;= the
    /// full budget, this reduces to full-budget-only in production — E2
    /// fires exactly on its motivating evidence (in-flight >= 10 s at stop)
    /// and never on the zero-push 1.5 s shortcut (whose max waste is 1.5 s
    /// regardless).</summary>
    public static readonly TimeSpan MinInFlightForFutility = TimeSpan.FromSeconds(10);

    public static bool ShouldAbandonImmediately(
        TimeSpan? inFlightElapsed, TimeSpan drainBudget)
        => inFlightElapsed is { } elapsed
            && elapsed >= drainBudget
            && elapsed >= MinInFlightForFutility;
}
