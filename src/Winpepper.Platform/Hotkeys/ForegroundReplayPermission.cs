using System.Runtime.InteropServices;

namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Non-destructively checks whether UIPI permits Space replay to the current
/// foreground process. Unknown targets and token-query failures fail closed.
/// Winpepper's manifest has uiAccess=false, so numeric integrity comparison is
/// the applicable SendInput boundary.
/// </summary>
internal static partial class ForegroundReplayPermission
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    private static readonly Lazy<uint?> CurrentIntegrity = new(TryGetCurrentIntegrity);

    internal static bool CanReplayToForeground()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return IsPermitted(CurrentIntegrity.Value, TryGetForegroundIntegrity());
    }

    internal static bool IsPermitted(uint? currentIntegrity, uint? targetIntegrity)
        => currentIntegrity.HasValue
           && targetIntegrity.HasValue
           && currentIntegrity.Value >= targetIntegrity.Value;

    private static uint? TryGetCurrentIntegrity()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return null;
        try { return TryGetIntegrityRid(token); }
        finally { CloseHandle(token); }
    }

    private static uint? TryGetForegroundIntegrity()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;
        if (GetWindowThreadProcessId(foreground, out var processId) == 0 || processId == 0)
            return null;
        if (processId == GetCurrentProcessId()) return CurrentIntegrity.Value;

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try { return TryGetIntegrityRid(token); }
            finally { CloseHandle(token); }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private static uint? TryGetIntegrityRid(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var required);
        if (required < IntPtr.Size) return null;

        var buffer = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, required, out _))
                return null;
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero || !IsValidSid(sid)) return null;
            var countPointer = GetSidSubAuthorityCount(sid);
            if (countPointer == IntPtr.Zero) return null;
            var count = Marshal.ReadByte(countPointer);
            if (count == 0) return null;
            var ridPointer = GetSidSubAuthority(sid, (uint)(count - 1));
            return ridPointer == IntPtr.Zero
                ? null
                : unchecked((uint)Marshal.ReadInt32(ridPointer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(
        IntPtr process,
        uint desiredAccess,
        out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsValidSid(IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [LibraryImport("advapi32.dll")]
    private static partial IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
