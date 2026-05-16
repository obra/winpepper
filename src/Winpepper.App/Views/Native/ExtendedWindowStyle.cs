#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ExtendedWindowStyle
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_ALPHA         = 0x00000002;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);

    public static void MakeClickThroughTopmostTool(IntPtr hwnd, byte alpha = 230)
    {
        // ORDER MATTERS: read existing styles, OR in LAYERED + TRANSPARENT + TOOLWINDOW + NOACTIVATE,
        // commit with SetWindowLongPtr BEFORE calling SetLayeredWindowAttributes.
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated  = existing | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }
}
#endif
