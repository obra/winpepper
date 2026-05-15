#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

[Trait("Platform", "Windows")]
public class OcrIntegrationTests
{
    [Fact]
    public async Task Capture_ZeroHandle_ReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ocr = new OcrFallback(new NullLogger<OcrFallback>());
        var result = await ocr.CaptureAsync(IntPtr.Zero, CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Capture_AnyForeground_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ocr = new OcrFallback(new NullLogger<OcrFallback>());
        var hwnd = ForegroundWindow.Handle();
        var result = await ocr.CaptureAsync(hwnd, CancellationToken.None);
        result.ShouldNotBeNull();
        // Result text can be empty on a blank VM screen; we just verify no throw.
    }
}
#endif
