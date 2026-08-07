using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Managed wrappers over MessageDeliveryNative, guarded by
/// OperatingSystem.IsWindows() so the pure routing/gating logic above them
/// is exercisable on Linux (everything fails closed off-Windows: the
/// ladder then degrades to the VkPacket floor = status quo). These are the
/// production defaults behind the per-strategy ctor seams.
/// </summary>
internal static class MessageDelivery
{
    /// <summary>Window class name of hwnd, or null when unavailable.</summary>
    public static string? ClassName(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return null;
        var buffer = new char[256];
        var length = MessageDeliveryNative.GetClassNameW((IntPtr)hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : null;
    }

    /// <summary>Side-effect-free EM_GETSEL probe via SMTO; true = the target answered within 150 ms.</summary>
    public static bool EmGetSelProbe(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.EM_GETSEL, IntPtr.Zero, IntPtr.Zero,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>One EM_REPLACESEL (wParam=1: undoable) carrying the whole chunk string; false = refused/timed out.</summary>
    public static bool SendReplaceSel(long hwnd, string chunk)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.EM_REPLACESEL, (IntPtr)1, chunk,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>One WM_CHAR for one UTF-16 code unit (lParam=1: repeat count); false = refused/timed out.</summary>
    public static bool SendCharSmto(long hwnd, ushort unit)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return false;
        return MessageDeliveryNative.SendMessageTimeout(
            (IntPtr)hwnd, MessageDeliveryNative.WM_CHAR, (IntPtr)unit, (IntPtr)1,
            MessageDeliveryNative.SMTO_ABORTIFHUNG, MessageDeliveryNative.SmtoTimeoutMs,
            out _) != IntPtr.Zero;
    }

    /// <summary>
    /// One focused-child sample: resolve the foreground window's GUI thread
    /// (GetWindowThreadProcessId) and read GetGUIThreadInfo(...).hwndFocus.
    /// 0 when anything along the chain is unavailable.
    /// </summary>
    public static long SampleFocusedChild(long foregroundHwnd)
    {
        if (!OperatingSystem.IsWindows() || foregroundHwnd == 0) return 0;
        var threadId = ElevationNative.GetWindowThreadProcessId((IntPtr)foregroundHwnd, out _);
        if (threadId == 0) return 0;
        var info = new MessageDeliveryNative.GUITHREADINFO
        {
            cbSize = (uint)Marshal.SizeOf<MessageDeliveryNative.GUITHREADINFO>(),
        };
        if (!MessageDeliveryNative.GetGUIThreadInfo(threadId, ref info)) return 0;
        return info.hwndFocus.ToInt64();
    }
}
