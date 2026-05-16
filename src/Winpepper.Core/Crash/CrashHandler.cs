using Microsoft.Extensions.Logging;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;

namespace Winpepper.Core.Crash;

/// <summary>
/// Routes unhandled exceptions through the standard pipeline: log → minidump
/// → ErrorBus → engine reset. Spec §9.3.
/// </summary>
public sealed class CrashHandler
{
    private readonly ICrashSink _sink;
    private readonly ErrorBus _bus;
    private readonly SessionEngine _engine;
    private readonly ILogger<CrashHandler> _log;

    public CrashHandler(ICrashSink sink, ErrorBus bus, SessionEngine engine, ILogger<CrashHandler> log)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool HandleUnhandled(Exception ex, bool fromTaskScheduler)
    {
        var source = fromTaskScheduler
            ? "TaskScheduler.UnobservedTaskException"
            : "AppDomain.UnhandledException";

        _log.LogCritical(ex, "Unhandled exception from {Source}", source);

        string? dumpPath = null;
        try { dumpPath = _sink.WriteDump(ex, source); }
        catch (Exception sinkEx) { _log.LogError(sinkEx, "MiniDump write failed"); }
        if (dumpPath is not null) _log.LogInformation("Minidump written to {Path}", dumpPath);

        _bus.Report(ErrorStage.Crash, ex, Guid.Empty);

        try
        {
            _sink.ResetSessionEngine(_engine);
            return true;
        }
        catch (Exception resetEx)
        {
            _log.LogCritical(resetEx, "SessionEngine reset failed; app will exit");
            return false;
        }
    }
}
