using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Winpepper.Core.Logging;

public static class WinpepperLogging
{
    public static ILoggerFactory Create(string logDirectory, bool debugConsole, LogLevel minimumLevel)
    {
        Directory.CreateDirectory(logDirectory);

        var serilogLevel = minimumLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };

        var template = "{Timestamp:yyyy-MM-ddTHH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(serilogLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "winpepper-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: template,
                shared: false);

        if (debugConsole)
        {
            config = config.WriteTo.Console(outputTemplate: template);
        }

        Log.Logger = config.CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false));
    }

    public static void Flush() => Log.CloseAndFlush();
}
