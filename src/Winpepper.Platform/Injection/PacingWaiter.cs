namespace Winpepper.Platform.Injection;

/// <summary>
/// Production pacing primitive for the guarded injection send (the default
/// behind TextInjector's injectable sleep seam). Thread.Sleep CANNOT pace
/// millisecond-precise waits: measured on the Windows gate host it quantizes
/// to the legacy ~15.6 ms timer resolution (Sleep(5) averaged ~15.5 ms; even
/// the old shipped Sleep(20) really waited ~31 ms; bleed-hardening ledger,
/// V1). A high-resolution waitable timer
/// (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win10 1803+) measured 5.2-5.4 ms
/// per 5 ms wait WITHOUT raising the process timer resolution (no
/// timeBeginPeriod; ledger B1/B3) -- so it is not exposed to the Win11
/// occluded-window resolution revocation a raised-resolution Sleep would
/// risk. At the current 14 ms production pause
/// (TextInjector.InterChunkPauseMs, render-rate pacing) the Thread.Sleep
/// fail-safe (~15.6 ms) overshoots by only ~11%, but the high-res timer
/// keeps the pace deliberate, and the fixed 5 ms probe in
/// InterChunkPacingWindowsTests still proves the fast path engages on the
/// gate host. Fail-safe: if the timer cannot be created or set, falls back
/// to Thread.Sleep -- pacing gets coarser (feed slower) but nothing breaks.
/// </summary>
internal static class PacingWaiter
{
    public static void Wait(int ms)
    {
        if (ms <= 0) return;
        if (!OperatingSystem.IsWindows() || !TryHighResolutionWait(ms))
            Thread.Sleep(ms);
    }

    private static bool TryHighResolutionWait(int ms)
    {
        var timer = PacingWaiterNative.CreateWaitableTimerExW(
            IntPtr.Zero, IntPtr.Zero,
            PacingWaiterNative.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            PacingWaiterNative.TIMER_ALL_ACCESS);
        if (timer == IntPtr.Zero) return false;
        try
        {
            var dueTime = -(long)ms * 10_000; // negative = relative, 100 ns units
            if (!PacingWaiterNative.SetWaitableTimer(
                    timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                return false;
            return PacingWaiterNative.WaitForSingleObject(timer, (uint)ms + 1000)
                   == PacingWaiterNative.WAIT_OBJECT_0;
        }
        finally
        {
            PacingWaiterNative.CloseHandle(timer);
        }
    }
}
