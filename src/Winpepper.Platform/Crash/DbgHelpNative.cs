#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Crash;

internal static partial class DbgHelpNative
{
    [Flags]
    public enum MINIDUMP_TYPE : uint
    {
        MiniDumpNormal = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithFullMemory = 0x00000002,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpWithThreadInfo = 0x00001000,
        MiniDumpWithProcessThreadData = 0x00010000,
    }

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        MINIDUMP_TYPE dumpType,
        IntPtr expParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetCurrentProcess();
}
#endif
