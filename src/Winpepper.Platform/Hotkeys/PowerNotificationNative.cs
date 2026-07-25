using System.Runtime.InteropServices;

namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// powrprof.dll suspend/resume notifications in CALLBACK mode.
///
/// DEVICE_NOTIFY_CALLBACK works WITHOUT a window - which matters here because a
/// MESSAGE-ONLY window does NOT receive the WM_POWERBROADCAST broadcast. (A
/// hidden TOP-LEVEL window DOES receive it - that is the documented fallback
/// if the Task 9 smoke ever falsifies callback delivery in this packaged
/// process.) The hook thread has a message loop but no window, so callback
/// mode is the mechanism that fits it.
/// </summary>
internal static partial class PowerNotificationNative
{
    public const uint DEVICE_NOTIFY_CALLBACK = 0x00000002;
    public const uint ERROR_SUCCESS = 0;

    /// <summary>
    /// ULONG DeviceNotifyCallbackRoutine(PVOID Context, ULONG Type, PVOID Setting).
    /// Must return ERROR_SUCCESS and must not block: it runs on a system thread.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        /// <summary>Function pointer to a <see cref="DeviceNotifyCallbackRoutine"/>.</summary>
        public IntPtr Callback;
        public IntPtr Context;
    }

    // No SetLastError: both APIs return their error code directly (a Win32
    // ULONG checked against ERROR_SUCCESS), so GetLastError is meaningless.
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient,
        out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
