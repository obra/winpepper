#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ForegroundWindow
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
#endif
