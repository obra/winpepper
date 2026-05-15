#if WINDOWS
namespace Winpepper.Platform.WindowContext;

public static class ForegroundWindow
{
    public static IntPtr Handle() => UiaNative.GetForegroundWindow();

    public static string Title(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buf = new char[512];
        var len = UiaNative.GetWindowTextW(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }
}
#endif
