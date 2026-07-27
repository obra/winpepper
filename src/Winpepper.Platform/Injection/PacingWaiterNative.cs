using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// kernel32 surface for the high-resolution waitable timer used by
/// <see cref="PacingWaiter"/>. CREATE_WAITABLE_TIMER_HIGH_RESOLUTION is
/// supported on Windows 10 1803+ (below the app's 10.0.19041 TFM floor).
/// Same LibraryImport style as <see cref="SendInputNative"/> — compiles on
/// both TFMs; only ever invoked behind OperatingSystem.IsWindows().
/// </summary>
internal static partial class PacingWaiterNative
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x001F0003;
    public const uint WAIT_OBJECT_0 = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr CreateWaitableTimerExW(
        IntPtr timerAttributes, IntPtr timerName, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(
        IntPtr timer, in long dueTime, int period,
        IntPtr completionRoutine, IntPtr argToCompletionRoutine,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);
}
