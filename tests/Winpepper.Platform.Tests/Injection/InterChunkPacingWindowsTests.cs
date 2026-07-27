using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinel for the 5 ms inter-chunk pause. The guarded send
/// paces through PacingWaiter (TextInjector's production sleep default),
/// whose high-resolution waitable timer measured 5.22-5.37 ms per 5 ms wait
/// on the gate host (bleed-hardening ledger, B1) -- raw Thread.Sleep(5) is
/// NOT usable (measured ~15.5 ms; ledger V1). If this test FAILS on the
/// Windows gate, the high-res timer path is not engaging (creation/set
/// failure => Thread.Sleep fallback at ~15.6 ms) and the real feed rate has
/// collapsed to ~513 code units/s (a 1000-unit paste would take ~2 s and
/// stall the UI thread that long on a pill click). That is HALT CONDITION 1:
/// STOP and report -- do not widen this threshold or swap in a spin-wait
/// without explicit approval.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class InterChunkPacingWindowsTests
{
    [Fact]
    public void PacingWaiter_5ms_AverageStaysNearRequest_HighResolutionTimer()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Measured 5.22-5.37 ms avg on the gate host (0/240 samples >= 10 ms;
        // ledger B1). 10 ms cleanly separates the high-res path from the
        // ~15.6 ms legacy-quantum fallback that would break the feed floor.
        avgMs.ShouldBeLessThan(10.0);
    }
}
