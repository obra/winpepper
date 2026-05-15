using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Public window-context prefetch. UIA-first, OCR fallback. Failures are silent
/// (spec §9.1 — cleanup runs without window context rather than surfacing an
/// error to the user). The orchestrator (CleanupRunner) imposes its own
/// 500 ms wait budget; this class returns whenever the chosen path completes.
/// </summary>
public sealed class WindowContextPrefetch
{
    private readonly Func<IntPtr, CancellationToken, Task<string?>> _readUia;
    private readonly Func<IntPtr, CancellationToken, Task<WindowContextResult>> _captureOcr;
    private readonly ILogger<WindowContextPrefetch> _log;

    public WindowContextPrefetch(
        Func<IntPtr, CancellationToken, Task<string?>> readUia,
        Func<IntPtr, CancellationToken, Task<WindowContextResult>> captureOcr,
        ILogger<WindowContextPrefetch> log)
    {
        _readUia = readUia;
        _captureOcr = captureOcr;
        _log = log;
    }

    public async Task<WindowContextResult> StartAsync(IntPtr foregroundHwnd, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return WindowContextResult.Empty;
        if (foregroundHwnd == IntPtr.Zero) return WindowContextResult.Empty;

        // UIA path.
        string? uia;
        try
        {
            uia = await _readUia(foregroundHwnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UIA prefetch threw; falling through to OCR");
            uia = null;
        }

        if (!string.IsNullOrEmpty(uia) && uia.Length >= UiaTreeOrdering.DefaultMinViableChars)
            return WindowContextResult.FromUia(uia);

        // OCR fallback.
        WindowContextResult ocr;
        try
        {
            ocr = await _captureOcr(foregroundHwnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OCR prefetch threw; window context unavailable");
            ocr = WindowContextResult.Empty;
        }

        return ocr;
    }

    /// <summary>
    /// Convenience factory for the production Windows build. The Linux build
    /// callers (Cli on Linux is a no-op build target anyway) can construct
    /// directly with no-op seams.
    /// </summary>
#if WINDOWS
    public static WindowContextPrefetch CreateWindows(
        UiaTreeReader uiaReader,
        OcrFallback ocrFallback,
        ILogger<WindowContextPrefetch> log)
    {
        return new WindowContextPrefetch(
            readUia: (hwnd, ct) => Task.Run(() =>
            {
                var elements = uiaReader.ReadForeground(hwnd, ct);
                return UiaTreeOrdering.Compose(elements);
            }, ct),
            captureOcr: (hwnd, ct) => ocrFallback.CaptureAsync(hwnd, ct),
            log: log);
    }
#endif
}
