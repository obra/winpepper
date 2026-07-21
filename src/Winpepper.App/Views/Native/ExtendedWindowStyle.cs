#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ExtendedWindowStyle
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_ALPHA         = 0x00000002;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static void MakeClickThroughTopmostTool(IntPtr hwnd, byte alpha = 230)
    {
        // ORDER MATTERS: read existing styles, OR in TOPMOST + LAYERED +
        // TRANSPARENT + TOOLWINDOW + NOACTIVATE, commit with SetWindowLongPtr
        // BEFORE calling SetLayeredWindowAttributes. WS_EX_TOPMOST is the
        // *style* bit; SetWindowPos(HWND_TOPMOST) is what actually inserts us
        // into the topmost band. We do both, and never activate/steal focus.
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated  = existing | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        AssertTopmost(hwnd);
    }

    /// <summary>
    /// Re-inserts the window at the top of the z-order (HWND_TOPMOST) without
    /// moving, resizing, or activating it. Cheap; safe to call on every show
    /// and on a periodic tick. Other topmost windows created later can sit
    /// above us, so callers should re-assert whenever the pill becomes visible
    /// and while it stays visible.
    /// </summary>
    public static void AssertTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
#endif
