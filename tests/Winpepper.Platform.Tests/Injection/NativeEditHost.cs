using System.Runtime.InteropServices;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Hosts a real Win32 EDIT control on a dedicated STA thread with a message
/// pump, for in-proc delivery-strategy tests (design doc § 4 Windows gate).
/// StartNonPumping creates the same windows but never pumps — the target for
/// the "SMTO must return false within <= 2x timeout" pipeline-never-hangs
/// pin. Uses built-in window classes (STATIC parent, EDIT child) so no class
/// registration is needed. Windows-only; callers self-guard.
/// </summary>
internal sealed partial class NativeEditHost : IDisposable
{
    public IntPtr ParentHwnd { get; private set; }
    public IntPtr EditHwnd { get; private set; }
    public uint ThreadId { get; private set; }

    private readonly bool _pump;
    private readonly ManualResetEventSlim _ready = new();
    // Deliberately NOT a WaitHandle-based primitive: an STA thread parked in
    // WaitHandle.WaitOne (what ManualResetEventSlim.Wait falls back to once
    // its spin budget is exhausted) can service pending sent messages via
    // the CLR's COM-interop wait path, which would silently turn this
    // "non-pumping" host into a pumping one and let SendMessageTimeout
    // succeed instead of timing out. A polled volatile flag + Thread.Sleep
    // never enters any wait state that is message-queue-aware, so the host
    // genuinely never dispatches WM_CHAR/EM_REPLACESEL while parked.
    private volatile bool _stopRequested;
    private Thread? _thread;

    private NativeEditHost(bool pump) => _pump = pump;

    public static NativeEditHost Start() => Launch(pump: true);

    public static NativeEditHost StartNonPumping() => Launch(pump: false);

    private static NativeEditHost Launch(bool pump)
    {
        var host = new NativeEditHost(pump);
        host._thread = new Thread(host.Run) { IsBackground = true, Name = "NativeEditHost" };
        host._thread.SetApartmentState(ApartmentState.STA);
        host._thread.Start();
        if (!host._ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("NativeEditHost failed to start within 10 s");
        return host;
    }

    private void Run()
    {
        ThreadId = GetCurrentThreadId();
        ParentHwnd = CreateWindowExW(0, "STATIC", "winpepper-edit-host",
            WS_OVERLAPPED, 0, 0, 400, 200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        EditHwnd = CreateWindowExW(0, "EDIT", "",
            WS_CHILD | WS_VISIBLE | ES_MULTILINE, 0, 0, 380, 180,
            ParentHwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        SetFocus(EditHwnd); // thread-local keyboard focus: GetGUIThreadInfo sees it
        _ready.Set();
        if (!_pump)
        {
            // Deliberately never pump: SMTO sends must time out. Poll a
            // volatile flag via Thread.Sleep -- see _stopRequested's doc
            // comment for why a WaitHandle-based wait is unsafe here.
            while (!_stopRequested)
                Thread.Sleep(20);
            return;
        }
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in msg);
            DispatchMessageW(in msg);
        }
    }

    /// <summary>Read the EDIT content (cross-thread WM_GETTEXT; requires the pumping host).</summary>
    public string ReadText()
    {
        var buffer = new char[4096];
        var length = GetWindowTextW(EditHwnd, buffer, buffer.Length);
        return new string(buffer, 0, length);
    }

    public void Dispose()
    {
        if (_pump && ThreadId != 0)
            PostThreadMessageW(ThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _stopRequested = true;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _ready.Dispose();
    }

    private const uint WS_OVERLAPPED = 0x00000000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint ES_MULTILINE = 0x0004;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
