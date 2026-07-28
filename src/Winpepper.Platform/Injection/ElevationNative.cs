using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Win32 surface for the foreground-window elevation probe
/// (paste-path-hardening). Same LibraryImport style as SendInputNative --
/// compiles on both TFMs; only ever invoked behind
/// OperatingSystem.IsWindows().
/// </summary>
internal static partial class ElevationNative
{
    /// <summary>Minimal access that succeeds across integrity levels for ordinary processes.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Token access required by GetTokenInformation.</summary>
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation (a single DWORD: nonzero = elevated).</summary>
    public const int TokenElevationClass = 20;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);
}
