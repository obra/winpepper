using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for injection pacing (retuned for deadline pacing,
/// 2026-07-28; made machine-relative 2026-08-24). Three machine-INDEPENDENT
/// guards run on every host:
/// (1) the pacing primitive must never be SLOWER than the plain Thread.Sleep
///     it fails back into (median-of-21 on both, same host, same moment);
/// (2) the per-chunk period FLOOR (bleed safety: feed &lt;= ~571 &lt;= 600
///     units/s) asserted on the fastest of several full runs — VM noise only
///     pushes timing UP, so the minimum-of-runs average is the truthful speed
///     and the floor still bites on genuinely-too-fast pacing;
/// (3) send-time compensation through the real injector, checked against a
///     ceiling derived on-machine from the pacer's own 9 ms vs 14 ms wait
///     medians (not a number calibrated on one dev box).
/// A host whose waitable-timer regime cannot distinguish a 9 ms from a 14 ms
/// wait (discrimination &lt; 2.5 ms — e.g. legacy-quantum CI VMs) has no
/// measuring power for compensation: the ceiling leg SKIPS LOUDLY there
/// instead of failing on VM jitter or being widened into uselessness.
/// The regime the repo enforces (windows-gate.sh) exports
/// WINPEPPER_PIN_TIMING_HOST=1, which converts every such skip into a hard
/// failure: on the pinned host "cannot measure" IS "broken".
/// </summary>
[Trait("Platform", "Windows")]
public sealed class InterChunkPacingWindowsTests
{
    [Fact]
    public void PacingWaiter_HighResProbe_NeverSlowerThanSleep_AndFastWhenQuantumCoarse()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) { Thread.Sleep(5); PacingWaiter.Wait(5); }

        var sleepMs = MedianWaitMs(21, 5, useSleep: true);
        var waiterMs = MedianWaitMs(21, 5, useSleep: false);

        // Machine-independent: the waiter includes the Sleep fail-safe, so it
        // must never be slower than plain Sleep on any host.
        waiterMs.ShouldBeLessThanOrEqualTo(sleepMs + 2.0,
            $"PacingWaiter slower than its own Thread.Sleep fallback (sleep(5)~{sleepMs:0.0}ms vs waiter(5)~{waiterMs:0.0}ms) — pacing path regression");

        if (sleepMs < 10.0) return; // host already has a fine sleep quantum; nothing to separate

        // Legacy-quantum host (Sleep(5) ≈ 15.6 ms): the high-res path must land
        // clearly below the legacy territory.
        if (waiterMs < 10.0) return; // high-res engaged; property held

        if (IsPinnedTimingHost)
        {
            waiterMs.ShouldBeLessThan(10.0,
                $"STOP and report: pinned-timing host lost high-res engagement (waiter(5)~{waiterMs:0.0}ms) — do not widen thresholds without explicit approval");
        }
        Assert.Skip(
            $"cannot verify high-res engagement here (sleep(5)~{sleepMs:0.0}ms vs waiter(5)~{waiterMs:0.0}ms) — Thread.Sleep fallback regime is production-accepted; skipping");
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

        // Warm-up run (JIT + timer state), untimed.
        injector.TryInjectGuarded(new string('a', 3 * TextInjector.ChunkCodeUnits))
            .ShouldBe(InjectionRunOutcome.Completed);

        const int periods = 40; // 41 chunks => 40 inter-chunk periods
        const int runs = 5;
        var text = new string('a', (periods + 1) * TextInjector.ChunkCodeUnits);
        var perRun = new double[runs];
        var minAvg = double.MaxValue;
        for (var r = 0; r < runs; r++)
        {
            var sw = Stopwatch.StartNew();
            injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);
            sw.Stop();
            perRun[r] = sw.Elapsed.TotalMilliseconds / periods;
            if (perRun[r] < minAvg) minAvg = perRun[r];
        }
        var runsReport = string.Join(", ", perRun.Select(v => $"{v:0.0}"));

        // FLOOR (bleed safety): every period is send + ceiling-rounded
        // remainder sleep. Upward noise only on VMs => the min-of-runs average
        // is the truthful fastest pace; half-millisecond grace for measurement
        // noise. Machine-independent: no host can make pacing artificially
        // FAST.
        minAvg.ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs - 0.5,
            $"pace floor violated: min-of-{runs}-runs avg period {minAvg
            :0.00}ms < {TextInjector.InterChunkPauseMs - 0.5:0.0}ms (per-run: {runsReport})");

        // CEILING (compensation proof), machine-relative: measure what the
        // production wait mechanism can express ON THIS HOST between a 9 ms
        // remainder wait and a 14 ms full-period wait. An uncompensated
        // injector burns simulatedSendMs + pacer(14); compensated is
        // simulatedSendMs + pacer(9) + 2.5ms grace.
        var pacer9 = MedianWaitMs(21, 9, useSleep: false);
        var pacer14 = MedianWaitMs(21, 14, useSleep: false);
        var discrimination = pacer14 - pacer9;
        if (discrimination >= 2.5)
        {
            var ceiling = simulatedSendMs + pacer9 + 2.5;
            minAvg.ShouldBeLessThan(ceiling,
                $"compensation invisible: min-of-{runs}-runs avg period {minAvg:0.00}ms >= ceiling {ceiling:0.0}ms " +
                $"(send {simulatedSendMs}ms + pacer(9)~{pacer9:0.0}ms + 2.5ms grace; per-run: {runsReport}) — " +
                "cross-check the high-res probe; do not widen without explicit approval");
            return;
        }

        if (IsPinnedTimingHost)
        {
            discrimination.ShouldBeGreaterThanOrEqualTo(2.5,
                $"STOP and report: pinned-timing host cannot discriminate pacer(9)~{pacer9:0.0}ms from pacer(14)~{pacer14:0.0}ms — do not widen without explicit approval");
        }
        Assert.Skip(
            $"host wait regime cannot discriminate pacer(9)~{pacer9:0.0}ms from pacer(14)~{pacer14:0.0}ms; compensation is unmeasurable here (floor already asserted) — skipping the ceiling leg");
    }

    private static bool IsPinnedTimingHost =>
        string.Equals(Environment.GetEnvironmentVariable("WINPEPPER_PIN_TIMING_HOST"), "1",
            StringComparison.Ordinal);

    /// <summary>Median elapsed milliseconds for an <paramref name="ms"/> wait,
    /// 50% percentile of <paramref name="samples"/> tries. Median, not mean:
    /// VM noise delays but never speeds up, so the middle sample is robust to
    /// isolated preemption spikes.</summary>
    private static double MedianWaitMs(int samples, int ms, bool useSleep)
    {
        var xs = new double[samples];
        for (var i = 0; i < samples; i++)
        {
            var sw = Stopwatch.StartNew();
            if (useSleep) Thread.Sleep(ms); else PacingWaiter.Wait(ms);
            sw.Stop();
            xs[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(xs);
        return xs[xs.Length / 2];
    }

    private static void BusyWaitMs(double ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(64);
    }
}
