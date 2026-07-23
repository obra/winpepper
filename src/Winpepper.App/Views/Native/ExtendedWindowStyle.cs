#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ExtendedWindowStyle
{
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMNCRP_DISABLED = 1;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_COLORKEY      = 0x00000001;
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
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int widthEllipse,
        int heightEllipse);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

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
        // Plain per-window alpha only. LWA_COLORKEY was tried for a capsule
        // silhouette but proved unreliable under DComp composition (keyed
        // pixels rendered as a solid box on the live desktop). The pill is
        // now a uniformly-painted window rounded by DWM instead — no
        // transparency tricks, no shadow (see RemoveSystemBorder).
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

    /// <summary>
    /// Toggle ONLY the WS_EX_TRANSPARENT bit at runtime. When clickThrough is
    /// false the pill receives mouse input (needed for the PENDING "click to
    /// paste" state); when true, clicks pass through as normal. WS_EX_NOACTIVATE
    /// is left untouched in BOTH states so clicking the pill never activates it
    /// or steals focus from the target field. Re-asserts topmost afterward
    /// because changing the ex-style can drop us out of the topmost band.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated = clickThrough
            ? existing | WS_EX_TRANSPARENT
            : existing & ~(long)WS_EX_TRANSPARENT;
        if (updated == existing) return;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        AssertTopmost(hwnd);
    }

    public static uint GetWindowDpi(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 96u : dpi;
    }

    public static void RemoveSystemBorder(IntPtr hwnd)
    {
        // Windows 11 can draw a one-pixel DWM border even when the presenter chrome is hidden.
        // This attribute is unavailable on Windows 10, where the rounded region remains the fallback.
        var color = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));

        // Disable ALL DWM non-client rendering — most importantly the system
        // DROP SHADOW. On a layered window DWM cannot composite the shadow and
        // its surface degenerates into an opaque light ROUNDED RECTANGLE
        // painted behind the pill (the "white rectangle" bug). The pill is a
        // transient overlay; it needs no system shadow.
        var ncPolicy = DWMNCRP_DISABLED;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref ncPolicy, sizeof(int));

        // Round the window's own corners. The pill window is painted a single
        // uniform colour edge-to-edge, so DWM's rounding IS the pill shape —
        // no shaped-window/colour-key tricks required.
        var corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>
    /// Clip the window to a true capsule that exactly matches its client rect.
    /// The corner diameter equals the shorter client side (the height for the
    /// wide pill), so the ends are full semicircles; the region bounds are the
    /// EXCLUSIVE client bounds (no +1 overshoot) so no un-rounded sliver leaks
    /// outside the capsule. Region is computed from the MEASURED client rect in
    /// physical pixels, so it is correct at any DPI and after any resize. Call
    /// after every ResizeClient and on every Show.
    /// </summary>
    public static bool ApplyRoundedRegion(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var windowRect) ||
            !GetClientRect(hwnd, out var clientRect))
            return false;

        var clientOrigin = new POINT();
        if (!ClientToScreen(hwnd, ref clientOrigin))
            return false;

        var geometry = Views.StatusPillRegionGeometry.Compute(
            windowLeft: windowRect.Left,
            windowTop: windowRect.Top,
            clientOriginX: clientOrigin.X,
            clientOriginY: clientOrigin.Y,
            clientWidth: clientRect.Right - clientRect.Left,
            clientHeight: clientRect.Bottom - clientRect.Top);

        var region = CreateRoundRectRgn(
            geometry.Left,
            geometry.Top,
            geometry.Right,
            geometry.Bottom,
            geometry.CornerDiameter,
            geometry.CornerDiameter);
        if (region == IntPtr.Zero)
            return false;

        if (SetWindowRgn(hwnd, region, redraw: true) != 0)
            return true;

        DeleteObject(region);
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }
}
#endif
