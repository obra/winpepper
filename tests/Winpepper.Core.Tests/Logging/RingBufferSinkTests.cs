using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

// Logging tests touch Serilog's static Log.Logger / Log.CloseAndFlush();
// serialize them so parallel runs do not tear each other's loggers down.
[Collection("Winpepper.Core.Logging")]
public class RingBufferSinkTests
{
    [Fact]
    public void Logger_Writes_Lines_Into_Buffer()
    {
        var buf = new LogRingBuffer(capacity: 10);
        using var factory = WinpepperLogging.CreateWithBuffer(
            Path.Combine(Path.GetTempPath(), $"wp-log-{Guid.NewGuid():N}"),
            debugConsole: false,
            minimumLevel: LogLevel.Information,
            buffer: buf);
        var log = factory.CreateLogger("test");

        log.LogInformation("hello {Who}", "world");

        var snap = buf.Snapshot();
        snap.Count.ShouldBeGreaterThan(0);
        snap[^1].Message.ShouldContain("hello world");
        snap[^1].Level.ShouldBe("INF");
    }

    [Fact]
    public void Below_Minimum_Level_Is_Filtered_Out()
    {
        var buf = new LogRingBuffer(capacity: 10);
        using var factory = WinpepperLogging.CreateWithBuffer(
            Path.Combine(Path.GetTempPath(), $"wp-log-{Guid.NewGuid():N}"),
            debugConsole: false,
            minimumLevel: LogLevel.Warning,
            buffer: buf);
        var log = factory.CreateLogger("test");

        log.LogInformation("ignored");
        log.LogWarning("kept");

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(1);
        snap[0].Message.ShouldContain("kept");
        snap[0].Level.ShouldBe("WRN");
    }
}
