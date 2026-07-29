using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelLoggingTests
{
    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    [Fact]
    public void StageTransitions_AreLoggedAtInformation()
    {
        var engine = new SessionEngine();
        var log = new ListLogger();
        var vm = new SessionViewModel(engine, new SynchronousUiThread(), log: log); // named: skips the IDelayScheduler? delays 3rd param

        engine.Apply(SessionEvent.StartRequested);   // Idle -> Recording
        engine.Apply(SessionEvent.StopRequested);    // Recording -> Transcribing

        log.Lines.ShouldContain(l => l.Contains("pill stage") && l.Contains("Recording"));
        log.Lines.ShouldContain(l => l.Contains("pill stage") && l.Contains("Transcribing"));
        log.Lines.Where(l => l.Contains("pill stage")).ShouldAllBe(l => l.StartsWith("[Information]"));
    }

    [Fact]
    public void NullLogger_IsFine_AndExistingCtorShapeStillWorks()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());

        engine.Apply(SessionEvent.StartRequested);

        vm.Stage.ShouldBe(SessionStage.Recording);
    }
}
