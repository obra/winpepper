#if WINDOWS
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Winpepper.Platform.WindowContext;

namespace Winpepper.Platform.Learning;

/// <summary>
/// Resolves the currently-focused UIA element and packages it as a
/// <see cref="FocusedElementSnapshot"/>. Spec §8.2 (1).
/// Also registers the live <c>AutomationElement</c> with the supplied
/// <see cref="UiaFocusedElementTextWatcher"/> so a later <c>Subscribe</c>
/// call can attach to it.
/// </summary>
public sealed class FocusedElementCapturer
{
    private readonly UiaFocusedElementTextWatcher _watcher;
    private readonly ILogger<FocusedElementCapturer> _log;

    public FocusedElementCapturer(
        UiaFocusedElementTextWatcher watcher,
        ILogger<FocusedElementCapturer> log)
    {
        _watcher = watcher;
        _log = log;
    }

    public FocusedElementSnapshot Capture()
    {
        IntPtr hwnd;
        try { hwnd = UiaNative.GetForegroundWindow(); }
        catch (Exception ex) { _log.LogDebug(ex, "GetForegroundWindow failed"); return FocusedElementSnapshot.Empty; }

        AutomationElement? focused = null;
        try { focused = AutomationElement.FocusedElement; }
        catch (Exception ex) { _log.LogDebug(ex, "AutomationElement.FocusedElement failed"); }

        if (focused is null) return FocusedElementSnapshot.Empty;

        int[]? runtimeId = null;
        try { runtimeId = focused.GetRuntimeId(); }
        catch (Exception ex) { _log.LogDebug(ex, "GetRuntimeId failed"); }
        var id = UiaFocusedElementCapture.RuntimeIdToString(runtimeId);
        if (string.IsNullOrEmpty(id)) return FocusedElementSnapshot.Empty;

        var title = "";
        try
        {
            var buf = new char[512];
            var len = UiaNative.GetWindowTextW(hwnd, buf, buf.Length);
            if (len > 0) title = new string(buf, 0, len);
        }
        catch { }

        _watcher.RegisterFocusedElement(id, focused);
        return new FocusedElementSnapshot { ForegroundHwnd = hwnd, ElementId = id, WindowTitle = title };
    }
}
#endif
