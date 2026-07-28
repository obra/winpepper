namespace Winpepper.Platform.Injection;

/// <summary>
/// Managed elevation probe for the foreground window at injection start
/// (TextInjector's production default behind the foregroundElevation seam).
/// Chain: HWND -> GetWindowThreadProcessId ->
/// OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) ->
/// OpenProcessToken(TOKEN_QUERY) -> GetTokenInformation(TokenElevation).
/// Validated on the gate host (paste-path-hardening probe evidence,
/// 2026-07-27): the full chain succeeds from medium IL against normal apps
/// (NotElevated) and against elevated user-session processes (Elevated);
/// SYSTEM/protected processes deny OpenProcess (err 5). Mapping:
/// - Window unobservable (hwnd 0, no PID, non-Windows, unexpected
///   exception) => Unknown -- the transient-observation fail-open bucket.
/// - PID obtained but ANY of OpenProcess / OpenProcessToken /
///   GetTokenInformation fails => Elevated -- the conservative bucket:
///   denial usually IS elevation, and parking never loses text while a
///   UIPI-swallowed SendInput loses all of it. (A process dying between the
///   PID lookup and OpenProcess also lands here; parking is still safe.)
/// Cost: ~3 us per call measured (budget &lt; 5 ms, once per injection start).
/// </summary>
internal static class ElevationProbe
{
    public static ForegroundElevation Probe(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return ForegroundElevation.Unknown;
        try
        {
            if (ElevationNative.GetWindowThreadProcessId((IntPtr)hwnd, out var pid) == 0 || pid == 0)
                return ForegroundElevation.Unknown; // window gone: observation failure -> fail open
            return ProbeProcessId(pid);
        }
        catch
        {
            return ForegroundElevation.Unknown; // unexpected managed failure: fail open
        }
    }

    internal static ForegroundElevation ProbeProcessId(uint pid)
    {
        var process = ElevationNative.OpenProcess(
            ElevationNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return ForegroundElevation.Elevated; // denied => conservative park
        try
        {
            if (!ElevationNative.OpenProcessToken(process, ElevationNative.TOKEN_QUERY, out var token))
                return ForegroundElevation.Elevated;
            try
            {
                if (!ElevationNative.GetTokenInformation(
                        token, ElevationNative.TokenElevationClass,
                        out var elevation, sizeof(int), out _))
                {
                    return ForegroundElevation.Elevated;
                }
                return elevation != 0
                    ? ForegroundElevation.Elevated
                    : ForegroundElevation.NotElevated;
            }
            finally
            {
                ElevationNative.CloseHandle(token);
            }
        }
        finally
        {
            ElevationNative.CloseHandle(process);
        }
    }
}
