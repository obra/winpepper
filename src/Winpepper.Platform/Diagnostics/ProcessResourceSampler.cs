using System.Runtime.InteropServices;

namespace Winpepper.Platform.Diagnostics;

/// <summary>Cheap per-recording resource reads for the dictation timing line
/// (page faults, system-wide CPU). Same LibraryImport style as
/// PacingWaiterNative: compiles on BOTH TFMs; every method returns null
/// off-Windows or on API failure so callers omit the field rather than fail a
/// dictation. Called only at recording start and at the stop request — never
/// on a hot path.</summary>
public static partial class ProcessResourceSampler
{
    public readonly record struct SystemTimesSample(long Idle100ns, long Kernel100ns, long User100ns);

    /// <summary>Process-lifetime page-fault count via GetProcessMemoryInfo
    /// (psapi). Callers diff two reads to get the recording-window delta.</summary>
    public static uint? PageFaultCount()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var counters = new PROCESS_MEMORY_COUNTERS_EX
        {
            cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>(),
        };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb)
            ? counters.PageFaultCount
            : null;
    }

    /// <summary>System-wide idle/kernel/user FILETIMEs in 100 ns units.
    /// Kernel INCLUDES idle — DictationTimingSummary.SystemCpuPercent does the
    /// subtraction. Caveat (doc-confirmed): on machines with more than 64
    /// logical processors, GetSystemTimes sums only the calling thread's
    /// primary processor group — sys_cpu then reflects one group, not the
    /// whole machine. Negligible on the target boxes.</summary>
    public static SystemTimesSample? SystemTimes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetSystemTimes(out var idle, out var kernel, out var user)
            ? new SystemTimesSample(idle, kernel, user)
            : null;
    }

    // internal (not private) so the layout/cb round-trip test can assert
    // Marshal.SizeOf == 80 on x64 (2 DWORDs + 9 SIZE_Ts, doc-verified).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;     // documented ALWAYS ZERO on Win7/2008R2 and earlier — never report this
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;      // use THIS for private/commit bytes if ever read from the struct
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(
        IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out long idleTime, out long kernelTime, out long userTime);
}
