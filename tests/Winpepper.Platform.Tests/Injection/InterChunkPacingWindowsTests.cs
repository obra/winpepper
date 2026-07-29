using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for injection pacing (retuned for deadline
/// pacing, 2026-07-28, superseding the paste-path-hardening raw-sleep-floor
/// sentinel). Two independent checks:
/// (1) a FIXED 5 ms probe that proves the high-resolution waitable timer
/// engages at all on this host -- Thread.Sleep(5) quantizes to ~15.6 ms
/// (ledger V1), so 10 ms cleanly separates the paths; a failure here means
/// the timer path is broken: STOP and report, do not widen the threshold or
/// swap in a spin-wait without explicit approval. The 5 ms probe is kept
/// SEPARATE from the production pace on purpose: near the 14 ms period the
/// high-res timer and the Sleep fallback are nearly indistinguishable on a
/// noisy host, so a production-pace measurement has no discriminating power
/// for timer health.
/// (2) a production-pace check that proves the per-chunk PERIOD floor AND
/// the send-time compensation through the real injector: with a simulated
/// 5 ms send, the average period must stay &gt;= InterChunkPauseMs (bleed
/// safety: feed &lt;= ~571 &lt;= 600 units/s) yet clearly BELOW what an
/// uncompensated full-pause injector would burn (~19.2 ms). Averaged over
/// 40 periods so single timer-quantum noise cannot trip either boundary.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class InterChunkPacingWindowsTests
{
    [Fact]
    public void PacingWaiter_5msProbe_HighResolutionTimerEngages()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) PacingWaiter.Wait(5);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PacingWaiter.Wait(5);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Measured 5.22-5.37 ms avg on the gate host (0/240 samples >= 10 ms;
        // bleed-hardening ledger B1). 10 ms cleanly separates the high-res
        // path from the ~15.6 ms legacy-quantum Thread.Sleep fallback.
        avgMs.ShouldBeLessThan(10.0);
    }

    [Fact]
    public void Injector_ProductionPace_PeriodFloor_SendTimeCompensated()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Simulated send cost, burned by a Stopwatch busy-wait (precise,
        // unlike Thread.Sleep). 5 ms sits inside the 14 ms period so BOTH
        // properties are observable: a nonzero remainder must be slept, and
        // the send time must be deducted from it.
        const double simulatedSendMs = 5.0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => { BusyWaitMs(simulatedSendMs); return true; },
            foregroundElevation: _ => ForegroundElevation.NotElevated);
        // sleep and monotonicMs stay at PRODUCTION defaults:
        // PacingWaiter.Wait + the Stopwatch-based monotonic clock.

        // Warm-up (JIT + timer state).
        injector.TryInjectGuarded(new string('a', 3 * TextInjector.ChunkCodeUnits))
            .ShouldBe(InjectionRunOutcome.Completed);

        const int periods = 40; // 41 chunks => 40 inter-chunk periods
        var text = new string('a', (periods + 1) * TextInjector.ChunkCodeUnits);
        var sw = Stopwatch.StartNew();
        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);
        sw.Stop();

        var avgPeriodMs = sw.Elapsed.TotalMilliseconds / periods;
        // FLOOR (bleed safety): every period is send + ceiling-rounded
        // remainder sleep, and the waitable timer's documented error mode
        // is expiration DELAY (never-early is not contractual, but early
        // firing has never been observed on this host and the period
        // carries 0.67 ms margin -- stage-2 ledger A1), so the per-period
        // floor holds on real timers (this assertion is its empirical pin);
        // half-millisecond grace for measurement noise (same convention as
        // the retired raw-sleep floor).
        avgPeriodMs.ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs - 0.5);
        // CEILING (compensation proof): an UNcompensated injector burns
        // simulatedSendMs + a full 14 ms sleep ~= 19.2+ ms/period (and the
        // Thread.Sleep fallback ~= 20.6). Compensated expectation is
        // ~14.2-14.7 ms, so 17.0 leaves ~2.3 ms of margin on a noisy host
        // while cleanly separating both failure modes. A failure at
        // ~19-21 ms means compensation is not happening or the high-res
        // timer is broken (cross-check the 5 ms probe): STOP and report --
        // do not widen the threshold without explicit approval.
        avgPeriodMs.ShouldBeLessThan(17.0);
    }

    private static void BusyWaitMs(double ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(64);
    }
}
