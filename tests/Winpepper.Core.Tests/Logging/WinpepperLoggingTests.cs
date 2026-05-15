using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class WinpepperLoggingTests : IDisposable
{
    private readonly string _dir;
    public WinpepperLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"winpepper-log-{Guid.NewGuid():N}");
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Create_WritesToFile_AndLogsLineAppears()
    {
        using var factory = WinpepperLogging.Create(_dir, debugConsole: false, minimumLevel: LogLevel.Information);
        var log = factory.CreateLogger("Test");
        log.LogInformation("hello {Token}", "world");
        WinpepperLogging.Flush();

        // Find the rolling file (winpepper-YYYYMMDD.log)
        var files = Directory.GetFiles(_dir, "winpepper-*.log");
        files.Length.ShouldBe(1);
        var contents = File.ReadAllText(files[0]);
        contents.ShouldContain("hello world");
    }
}
