using Shouldly;
using Winpepper.Platform.Diagnostics;
using Xunit;

namespace Winpepper.Platform.Tests.Diagnostics;

public class ProcessResourceSamplerTests
{
    [Fact]
    public void OffWindows_ReturnsNull_NeverThrows()
    {
        if (OperatingSystem.IsWindows()) return; // Windows behavior is gate-verified
        ProcessResourceSampler.PageFaultCount().ShouldBeNull();
        ProcessResourceSampler.SystemTimes().ShouldBeNull();
    }

    [Fact]
    public void OnWindows_ReturnsValues_AndSysCpuIsPlausible()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A cb/layout mistake makes GetProcessMemoryInfo return false -> null.
        ProcessResourceSampler.PageFaultCount().ShouldNotBeNull();
        var s0 = ProcessResourceSampler.SystemTimes();
        s0.ShouldNotBeNull();
        Thread.Sleep(50);
        var s1 = ProcessResourceSampler.SystemTimes();
        s1.ShouldNotBeNull();
        // Plausibility bound (upgraded 2026-07-30): `kernel + user > 0` alone
        // cannot catch a wrong busy subtraction. A real two-sample window
        // must satisfy the doc-confirmed structural invariants — kernel
        // INCLUDES idle, so 0 <= busy <= total and 0 <= sys_cpu <= 100.
        var idleD = s1!.Value.Idle100ns - s0!.Value.Idle100ns;
        var kernelD = s1.Value.Kernel100ns - s0.Value.Kernel100ns;
        var userD = s1.Value.User100ns - s0.Value.User100ns;
        var busy = kernelD - idleD + userD;
        var total = kernelD + userD;
        total.ShouldBeGreaterThan(0);
        busy.ShouldBeGreaterThanOrEqualTo(0); // idleΔ <= kernelΔ (kernel includes idle)
        (busy * 100 / total).ShouldBeInRange(0, 100); // same math as SystemCpuPercent
    }

    [Fact]
    public void MemoryCountersStruct_CbRoundTrip_MatchesDocumentedLayout()
    {
        // PROCESS_MEMORY_COUNTERS_EX: 2 DWORDs + 9 SIZE_Ts = 80 bytes on x64
        // (doc-verified layout; the 2 leading DWORDs make the SIZE_Ts
        // naturally 8-aligned — no hidden padding). cb and the function's cb
        // argument must both carry this exact value or the call fails/truncates.
        if (!Environment.Is64BitProcess) return;
        System.Runtime.InteropServices.Marshal
            .SizeOf<ProcessResourceSampler.PROCESS_MEMORY_COUNTERS_EX>()
            .ShouldBe(80);
    }
}
