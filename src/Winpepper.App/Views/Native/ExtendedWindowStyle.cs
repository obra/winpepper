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
    public const int GWL_STYLE   = -16;
    public const int GWL_EXSTYLE = -20;
    private const long WS_CAPTION    = 0x00C00000; // WS_BORDER | WS_DLGFRAME
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_SYSMENU    = 0x00080000;
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_COLORKEY      = 0x00000001;
    public const int LWA_ALPHA         = 0x00000002;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOZORDER     = 0x0004;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

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

    /// <summary>
    /// Diagnostics from the most recent <see cref="RemoveSystemBorder"/> call:
    /// DWM HRESULTs, the stripped style bits, and the window-vs-client rect
    /// inset. Logged by the pill preview harness so the on-device probe can see
    /// WHY an edge artifact exists instead of guessing. Empirically the pill's
    /// white edge ring matched a non-client inset (client rect smaller than the
    /// window rect), i.e. residual frame — not app-painted content.
    /// </summary>
    public static string LastBorderDiagnostics { get; private set; } = "";

    public static void RemoveSystemBorder(IntPtr hwnd)
    {
        // Strip every classic frame style. The pill previously kept residual
        // WS frame bits (WinUI's OverlappedPresenter hides chrome but does not
        // necessarily clear them), leaving a 2-3px non-client ring that DWM
        // paints light — the owner-visible "white lines around the edges".
        var style = (long)GetWindowLongPtr64(hwnd, GWL_STYLE);
        var strippedStyle = style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU);
        if (strippedStyle != style)
        {
            SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(strippedStyle));
            // Style changes take effect only after a frame-changed poke.
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // Windows 11 can draw a one-pixel DWM border even when the presenter chrome is hidden.
        // This attribute is unavailable on Windows 10, where the rounded region remains the fallback.
        var color = DWMWA_COLOR_NONE;
        var hrBorder = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));

        // IMPORTANT: do NOT set DWMWA_NCRENDERING_POLICY = DISABLED here. DWM's
        // non-client rendering implements the corner rounding below; disabling
        // it is a heavier hammer than the frame-style strip above.

        // Round the window's own corners. The pill window is painted a single
        // uniform colour edge-to-edge, so DWM's rounding IS the pill shape —
        // no shaped-window/colour-key tricks required.
        var corner = DWMWCP_ROUND;
        var hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Record what actually happened for the preview harness to log.
        var insetText = "rects unavailable";
        if (GetWindowRect(hwnd, out var wr) && GetClientRect(hwnd, out var cr))
        {
            var origin = new POINT();
            if (ClientToScreen(hwnd, ref origin))
            {
                insetText =
                    $"window {wr.Right - wr.Left}x{wr.Bottom - wr.Top} at {wr.Left},{wr.Top}; " +
                    $"client {cr.Right - cr.Left}x{cr.Bottom - cr.Top} at {origin.X},{origin.Y}; " +
                    $"inset L{origin.X - wr.Left} T{origin.Y - wr.Top} " +
                    $"R{wr.Right - (origin.X + cr.Right - cr.Left)} B{wr.Bottom - (origin.Y + cr.Bottom - cr.Top)}";
            }
        }
        LastBorderDiagnostics =
            $"style 0x{style:X8} -> 0x{strippedStyle:X8}; hrBorderColor=0x{hrBorder:X8}; hrCorner=0x{hrCorner:X8}; {insetText}";
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
