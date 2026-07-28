using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for the elevation probe, pinning the
/// paste-path-hardening probe evidence (2026-07-27): from medium IL the
/// TokenElevation chain succeeds against normal user processes
/// (NotElevated) and elevated user-session processes (Elevated observed on
/// the gate host); SYSTEM/protected processes deny OpenProcess (err 5),
/// which the probe maps to Elevated -- the conservative park that never
/// loses text. Measured cost ~3 us/call (budget < 5 ms per injection
/// start).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class ElevationProbeWindowsTests
{
    [Fact]
    public void ProbeProcessId_OwnNonElevatedProcess_ReportsNotElevated()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The gate normally runs non-elevated; if someone runs it elevated
        // the not-elevated fixture simply is not available.
        Assert.SkipWhen(Environment.IsPrivilegedProcess,
            "gate host is running elevated; not-elevated fixture unavailable");

        ElevationProbe.ProbeProcessId((uint)Environment.ProcessId)
            .ShouldBe(ForegroundElevation.NotElevated);
    }

    [Fact]
    public void ProbeProcessId_ProtectedSystemProcess_ReportsElevated_ViaConservativeDenial()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipWhen(Environment.IsPrivilegedProcess,
            "an elevated runner could open winlogon for real; the denial path needs medium IL");

        // winlogon denies OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) to
        // medium IL (err 5 measured on the gate host); the probe must map
        // denial to Elevated so text is parked rather than silently dropped.
        var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault();
        Assert.SkipWhen(winlogon is null, "no winlogon process visible");

        ElevationProbe.ProbeProcessId((uint)winlogon!.Id)
            .ShouldBe(ForegroundElevation.Elevated);
    }

    [Fact]
    public void ProbeProcessId_PerCallCost_WellUnderInjectionBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = (uint)Environment.ProcessId;
        for (var i = 0; i < 10; i++) ElevationProbe.ProbeProcessId(pid); // warm-up

        const int iterations = 200;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) ElevationProbe.ProbeProcessId(pid);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Measured ~0.003 ms/call on the gate host; the spec budget is
        // < 5 ms once per injection start (never per chunk).
        avgMs.ShouldBeLessThan(5.0);
    }
}
