using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextPrefetchTests
{
    private static WindowContextPrefetch NewPrefetch(
        Func<IntPtr, CancellationToken, Task<string?>>? uia = null,
        Func<IntPtr, CancellationToken, Task<WindowContextResult>>? ocr = null) =>
        new(
            readUia: uia ?? ((_, _) => Task.FromResult<string?>(null)),
            captureOcr: ocr ?? ((_, _) => Task.FromResult(WindowContextResult.Empty)),
            log: new NullLogger<WindowContextPrefetch>());

    [Fact]
    public async Task Start_UiaReturnsLongEnoughText_UsesUiaPath()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => Task.FromResult<string?>(new string('x', 200)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Uia);
        result.Text.Length.ShouldBe(200);
    }

    [Fact]
    public async Task Start_UiaReturnsShortText_FallsThroughToOcr()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => Task.FromResult<string?>("hi"),
            ocr: (_, _) => Task.FromResult(WindowContextResult.FromOcr("plenty of OCR text here", 0.9)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Ocr);
    }

    [Fact]
    public async Task Start_UiaThrows_FallsThroughToOcr_Silently()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => throw new InvalidOperationException("boom"),
            ocr: (_, _) => Task.FromResult(WindowContextResult.FromOcr("recovered", 0.7)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Ocr);
        result.Text.ShouldBe("recovered");
    }

    [Fact]
    public async Task Start_BothFail_ReturnsEmpty()
    {
        var prefetch = NewPrefetch();
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Start_BothThrow_ReturnsEmpty()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => throw new InvalidOperationException("u"),
            ocr: (_, _) => throw new InvalidOperationException("o"));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Start_Cancelled_BeforeUia_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var prefetch = NewPrefetch();
        var result = await prefetch.StartAsync(new IntPtr(0x1234), cts.Token);
        result.ShouldBe(WindowContextResult.Empty);
    }
}
