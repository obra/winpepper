using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Linux-runnable coverage for the elevation probe's fail-open envelope.
/// The real Win32 chain is pinned by ElevationProbeWindowsTests on the gate.
/// </summary>
public sealed class ElevationProbeTests
{
    [Fact]
    public void Probe_ZeroHwnd_ReturnsUnknown_FailOpen()
    {
        // No observable foreground window: transient observation failure.
        ElevationProbe.Probe(0).ShouldBe(ForegroundElevation.Unknown);
    }

    [Fact]
    public void Probe_OffWindows_ReturnsUnknown_FailOpen()
    {
        // On non-Windows the probe can never observe; it must fail open like
        // TextInjector.DefaultForegroundProbe (returns 0) rather than park.
        if (OperatingSystem.IsWindows()) return;
        ElevationProbe.Probe(42).ShouldBe(ForegroundElevation.Unknown);
    }
}
