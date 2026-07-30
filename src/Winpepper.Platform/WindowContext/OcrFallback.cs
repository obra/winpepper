#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// OCR fallback for window-context prefetch. Spec §6.1. Captures the foreground
/// window's client area via <c>PrintWindow</c>, hands the bitmap to
/// <c>Windows.Media.Ocr.OcrEngine</c>, sorts the results in reading order,
/// truncates to 4000 chars.
/// </summary>
public sealed class OcrFallback
{
    private readonly ILogger<OcrFallback> _log;

    public OcrFallback(ILogger<OcrFallback> log) { _log = log; }

    public async Task<WindowContextResult> CaptureAsync(IntPtr foregroundHwnd, CancellationToken ct)
    {
        if (foregroundHwnd == IntPtr.Zero) return WindowContextResult.Empty;

        // D1: the capture stage (PrintWindow etc.) is blocking native work
        // that cannot observe ct mid-call — at least skip it entirely when
        // this dictation's prefetch is already cancelled.
        ct.ThrowIfCancellationRequested();

        if (!PrintWindowNative.GetClientRect(foregroundHwnd, out var rect)) return WindowContextResult.Empty;
        var w = rect.Width;
        var h = rect.Height;
        if (w <= 0 || h <= 0) return WindowContextResult.Empty;

        SoftwareBitmap? swBitmap;
        try
        {
            swBitmap = CaptureWindowToSoftwareBitmap(foregroundHwnd, w, h);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PrintWindow capture failed");
            return WindowContextResult.Empty;
        }
        if (swBitmap is null) return WindowContextResult.Empty;

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            _log.LogDebug("OcrEngine.TryCreateFromUserProfileLanguages returned null; no OCR languages installed");
            swBitmap.Dispose(); // C2: no native op ever touched it — safe to close here
            return WindowContextResult.Empty;
        }

        OcrResult ocr;
        try
        {
            // C2: dispose is owned by a CONTINUATION on a TOKEN-LESS
            // AsTask(): that Task reaches terminal state ONLY via the
            // WinRT op's Completed handler, so the bitmap is never closed
            // while the native op may still read it. Passing ct to
            // AsTask would be a use-after-close: the CsWinRT bridge
            // (WinRT.Runtime 2.2.0) cancels the TASK eagerly while the
            // native op keeps running (Cancel() is advisory).
            var op = engine.RecognizeAsync(swBitmap);
            var opTask = op.AsTask(); // NO token — terminal only from Completed
            using var reg = ct.Register(() =>
            {
                try { op.Cancel(); } catch { /* COM disconnect */ }
            });
            _ = opTask.ContinueWith(
                _ => swBitmap.Dispose(), // runs only after the op is observably terminal
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            // WaitAsync(ct): the CALLER still unblocks promptly on ct
            // (D1's routine cancellation stays fast); the op + dispose
            // continuation finish in the background on their own schedule.
            ocr = await opTask.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // prompt cancel for the caller; dispose continuation still pending
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OcrEngine.RecognizeAsync threw");
            return WindowContextResult.Empty;
        }

        var lines = ocr.Lines.Select(l => new OcrLineSort.Line(
            Top: (int)(l.Words.Count > 0 ? l.Words[0].BoundingRect.Top : 0),
            Words: l.Words.Select(w => new OcrLineSort.Word(
                Left: (int)w.BoundingRect.Left,
                Text: w.Text,
                Confidence: 1.0)).ToList())).ToList();

        var text = OcrLineSort.SortAndJoin(lines);
        var confidence = OcrLineSort.AverageConfidence(lines);
        _log.LogDebug("OCR recovered {Chars} chars, avg confidence {Conf:F2}", text.Length, confidence);

        return text.Length == 0
            ? WindowContextResult.Empty
            : WindowContextResult.FromOcr(text, confidence);
    }

    private static SoftwareBitmap? CaptureWindowToSoftwareBitmap(IntPtr hwnd, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindowNative.PrintWindow(hwnd, hdc, PrintWindowNative.PW_RENDERFULLCONTENT))
                    return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var sw = SoftwareBitmap.CreateCopyFromBuffer(
                buffer.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                bmp.Width,
                bmp.Height,
                BitmapAlphaMode.Premultiplied);
            return sw;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
#endif
