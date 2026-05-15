#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

[Trait("Platform", "Windows")]
public class UiaIntegrationTests
{
    [Fact]
    public void ReadForeground_OnAnyForegroundWindow_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = ForegroundWindow.Handle();
        var reader = new UiaTreeReader(new NullLogger<UiaTreeReader>());
        var result = reader.ReadForeground(hwnd, CancellationToken.None);
        result.ShouldNotBeNull();
        // Result count is environment-dependent on a headless VM; we only verify no throw.
    }

    [Fact]
    public void ReadForeground_ZeroHandle_ReturnsEmptyList()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new UiaTreeReader(new NullLogger<UiaTreeReader>());
        var result = reader.ReadForeground(IntPtr.Zero, CancellationToken.None);
        result.ShouldBeEmpty();
    }
}
#endif
