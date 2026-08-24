#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Tests.TestInfra;

/// <summary>
/// A real Win32 window the test OWNS, with a deterministic 3-node UIA tree
/// (top-level window + one multiline EDIT child holding <see cref="SentinelText"/>).
///
/// Why this exists (2026-08-24 investigation, evidence in
/// artifacts/read-probe/probe.tsv + probe2.tsv): the gate's real-UIA/OCR facts
/// used to read whatever window the USER happened to have focused on the gate
/// host at run time. Read cost scales with the focused window's UIA tree size
/// (3 nodes ~= 10-30 ms under host load; Chrome pages ~= 0.5-3 s), and with
/// foreground-app responsiveness (a starved Electron provider stalled reads for
/// 10-21 s). That made the facts pass/fail on ambient focus + host load, which
/// they cannot control — the Aug-13 + Aug-24 gate reds. A test-owned window
/// keeps the full REAL machinery coverage (real UIA walk, real cross-pattern
/// extraction via UiaTreeReader/UiaTreeOrdering, real OCR when needed) while
/// making the observed cost deterministic and tiny on any load.
///
/// Implementation notes:
/// - The window is created on a dedicated background STA thread with a message
///   pump, so the EDIT control's UIA provider always has a responsive thread.
/// - Focus theft and desktop flash are both avoided: the window is placed
///   off-screen and shown with SW_SHOWNA (no activation). The OS visibility
///   bit is REQUIRED: UIA's default hwnd provider skips child enumeration for
///   windows that were never shown (verified 2026-08-24: never-shown window
///   exposes only its TitleBar pseudo-element; EnumChildWindows still sees the
///   EDIT — UIA drops it). Off-screen + SW_SHOWNA flips the visibility bit
///   without the user ever seeing or focusing the window.
/// - Destroy on Dispose, best-effort; process exit cleans up regardless.
/// </summary>
internal sealed class TestOwnedWindow : IDisposable
{
    /// <summary>Deterministic >=80-char text held by the window's EDIT child —
    /// what the real UIA walk must recover (UiaTreeOrdering viability floor is
    /// 80 chars, so the read must exercise the real text patterns to pass).</summary>
    public const string SentinelText =
        "winpepper-owned-window-context sentinel: this deterministic text proves the real " +
        "UIA walk extracted content from a window the test controls";

    private const string ClassName = "WinpepperTestOwnedWindow";
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const int SW_SHOWNA = 8;

    public IntPtr Hwnd { get; }

    private readonly Thread _thread;

    private TestOwnedWindow(IntPtr hwnd, Thread thread)
    {
        Hwnd = hwnd;
        _thread = thread;
    }

    // Rooted for the process lifetime: an unrooted delegate can be collected
    // while native code still holds the WNDCLASS pointer to it.
    private static readonly WndProcDelegate s_wndProc = WndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint minFilter, uint maxFilter);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY) PostQuitMessage(0);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Creates the owned window (hidden, never activated). Returns null when the
    /// session cannot create windows (e.g. a future headless context) — callers
    /// should SkipUnless on null so the gate log states the gap honestly.
    /// </summary>
    public static TestOwnedWindow? Create()
    {
        var ready = new ManualResetEventSlim();
        var hwnd = IntPtr.Zero;

        var thread = new Thread(() =>
        {
            var wc = new WNDCLASS { lpfnWndProc = s_wndProc, lpszClassName = ClassName };
            _ = RegisterClassW(ref wc); // ERROR_CLASS_ALREADY_EXISTS on repeats is expected; ignore

            hwnd = CreateWindowExW(
                0, ClassName, "winpepper test-owned window (deterministic UIA tree)",
                WS_OVERLAPPEDWINDOW, -32000, -32000, 640, 480,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (hwnd != IntPtr.Zero)
            {
                // The deterministic 3rd tree node: a multiline EDIT child holding
                // the sentinel text. user32 EDIT controls expose Value/Text
                // patterns to UIA without activation.
                _ = CreateWindowExW(
                    0, "EDIT", SentinelText,
                    WS_CHILD | WS_VISIBLE | ES_MULTILINE, 0, 0, 620, 440,
                    hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

                // Required for UIA to enumerate the client children (see class
                // doc); SW_SHOWNA marks visible WITHOUT activation/focus change.
                ShowWindow(hwnd, SW_SHOWNA);
            }

            ready.Set();
            if (hwnd == IntPtr.Zero) return;

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        })
        { IsBackground = true, Name = "TestOwnedWindow-msgpump" };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)) || hwnd == IntPtr.Zero) return null;
        return new TestOwnedWindow(hwnd, thread);
    }

    public void Dispose()
    {
        // WM_CLOSE -> DefWindowProc -> DestroyWindow -> WM_DESTROY -> PostQuitMessage
        // -> pump exits -> thread terminates.
        PostMessageW(Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(10));
    }
}
#endif
