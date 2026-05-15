#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.WindowContext;

internal static partial class UiaNative
{
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
#endif
