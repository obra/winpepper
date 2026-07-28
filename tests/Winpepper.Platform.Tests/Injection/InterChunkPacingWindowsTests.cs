using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for injection pacing (retuned by
/// paste-path-hardening, 2026-07-27, superseding the bleed-hardening
/// sentinel with explicit owner approval). Two independent checks:
/// (1) a FIXED 5 ms probe that proves the high-resolution waitable timer
/// engages at all on this host -- Thread.Sleep(5) quantizes to ~15.6 ms
/// (ledger V1), so 10 ms cleanly separates the paths; a failure here means
/// the timer path is broken: STOP and report, do not widen the threshold or
/// swap in a spin-wait without explicit approval. The 5 ms probe is kept
/// SEPARATE from the production pause on purpose: at 14 ms the high-res
/// timer (~14.2 ms; a waitable timer never signals before its due time,
/// and the 5 ms probe measured +0.2-0.4 ms overshoot) and the Sleep
/// fallback (~15.6 ms) are nearly indistinguishable on a noisy host, so a
/// production-pace measurement has no discriminating power for timer health.
/// (2) a production-pace check that proves the NEW render-rate floor: the
/// inter-chunk wait really is at least InterChunkPauseMs, so the feed rate
/// stays at or below ~571 code units/s and the bleed backlog cannot build
/// against slow-rendering (~600 chars/s) target apps.
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
    public void PacingWaiter_ProductionPace_WaitsAtLeastTheRequestedPause()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // THE new floor (paste-path-hardening): the pace must really be at
        // least ~14 ms so the feed stays at or below ~571 code units/s and
        // backlog cannot grow against slow-rendering apps. Half-millisecond
        // grace for timer coalescing on a noisy host.
        avgMs.ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs - 0.5);
        // Sanity ceiling: even the Thread.Sleep fallback lands ~15.6 ms;
        // past 20 ms something new is broken (feed < 400 units/s).
        avgMs.ShouldBeLessThan(20.0);
    }
}
