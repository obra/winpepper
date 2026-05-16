using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Core.Crash;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Xunit;

namespace Winpepper.Core.Tests.Crash;

public class CrashHandlerTests
{
    [Fact]
    public void OnUnhandled_Writes_Dump_Logs_Bus_And_Tries_Engine_Reset()
    {
        var engine = new SessionEngine();
        engine.Apply(SessionEvent.StartRequested);
        var bus = new ErrorBus();
        var sink = new FakeCrashSink { WriteDumpResult = "C:\\dumps\\one.dmp" };
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        var ex = new InvalidOperationException("boom");
        var keepAlive = handler.HandleUnhandled(ex, fromTaskScheduler: false);

        sink.WroteDump.ShouldBeTrue();
        engine.State.ShouldBe(SessionState.Idle);
        bus.MostRecent()?.Stage.ShouldBe(ErrorStage.Crash);
        keepAlive.ShouldBeTrue();
    }

    [Fact]
    public void OnUnhandled_Returns_False_When_Reset_Fails()
    {
        var engine = new SessionEngine();
        var bus = new ErrorBus();
        var sink = new FakeCrashSink { WriteDumpResult = null, ThrowOnReset = true };
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        var keepAlive = handler.HandleUnhandled(new Exception("x"), fromTaskScheduler: true);

        keepAlive.ShouldBeFalse();
    }

    [Fact]
    public void OnUnhandled_Sets_Reset_Source_Tag_From_Caller()
    {
        var engine = new SessionEngine();
        var bus = new ErrorBus();
        var sink = new FakeCrashSink();
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        handler.HandleUnhandled(new Exception("a"), fromTaskScheduler: true);
        sink.LastSource.ShouldBe("TaskScheduler.UnobservedTaskException");

        handler.HandleUnhandled(new Exception("b"), fromTaskScheduler: false);
        sink.LastSource.ShouldBe("AppDomain.UnhandledException");
    }

    private sealed class FakeCrashSink : ICrashSink
    {
        public bool WroteDump { get; private set; }
        public string? WriteDumpResult { get; set; }
        public bool ThrowOnReset { get; set; }
        public string? LastSource { get; private set; }

        public string? WriteDump(Exception ex, string source)
        {
            WroteDump = true;
            LastSource = source;
            return WriteDumpResult;
        }

        public void ResetSessionEngine(SessionEngine engine)
        {
            if (ThrowOnReset) throw new InvalidOperationException("reset failed");
            engine.Apply(SessionEvent.CancelRequested);
        }
    }
}
