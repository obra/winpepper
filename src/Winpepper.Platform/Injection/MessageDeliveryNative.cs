using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// P/Invoke surface (user32) for the message-based delivery rungs and the
/// focused-child capture (design doc §2.2): GetGUIThreadInfo for the
/// double-sample, GetClassNameW for the rung-1 gate, and SendMessageTimeoutW
/// (IntPtr and string lParam overloads) for EM_GETSEL / EM_REPLACESEL /
/// WM_CHAR sends. All calls are made only behind OperatingSystem.IsWindows()
/// runtime checks in MessageDelivery.
/// </summary>
internal static partial class MessageDeliveryNative
{
    public const uint EM_GETSEL = 0x00B0;
    public const uint EM_REPLACESEL = 0x00C2;
    public const uint WM_CHAR = 0x0102;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Pinned SMTO timeout for both gates and sends (design doc §2.2: "SMTO, 150 ms").</summary>
    public const uint SmtoTimeoutMs = 150;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO pgui);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
