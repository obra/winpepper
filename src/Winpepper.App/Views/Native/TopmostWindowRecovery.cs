namespace Winpepper.App.Views.Native;

internal readonly record struct WindowPositionResult(bool Succeeded, int Error);

internal readonly record struct TopmostRecoveryResult(
    long InitialStyle,
    long FinalStyle,
    WindowPositionResult InitialRequest,
    WindowPositionResult? ResetRequest = null,
    WindowPositionResult? RetryRequest = null)
{
    public bool RecoveryAttempted => ResetRequest.HasValue;
    public bool IsTopmost => (FinalStyle & TopmostWindowRecovery.TopmostStyle) != 0;
}

internal static class TopmostWindowRecovery
{
    internal const long TopmostStyle = 0x00000008;

    public static TopmostRecoveryResult Assert(
        IntPtr hwnd,
        Func<IntPtr, long> readStyle,
        Func<IntPtr, bool, WindowPositionResult> setTopmost)
    {
        var initial = setTopmost(hwnd, true);
        var style = readStyle(hwnd);
        if ((style & TopmostStyle) != 0)
            return new(style, style, initial);

        // A tray-only WinUI window can report success from HWND_TOPMOST while
        // remaining outside the topmost band. Repeating that request does not
        // repair it; explicitly leave the band before requesting it again.
        var reset = setTopmost(hwnd, false);
        var retry = setTopmost(hwnd, true);
        return new(style, readStyle(hwnd), initial, reset, retry);
    }
}
