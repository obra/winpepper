using Shouldly;
using Winpepper.Core.Sessions;
using Xunit;

namespace Winpepper.Core.Tests.Sessions;

public class SessionEngineTests
{
    [Fact]
    public void Idle_ReceivesStart_GoesToRecording()
    {
        var e = new SessionEngine();
        e.State.ShouldBe(SessionState.Idle);
        e.Apply(SessionEvent.StartRequested);
        e.State.ShouldBe(SessionState.Recording);
    }

    [Fact]
    public void Recording_ReceivesStop_GoesToTranscribing()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.State.ShouldBe(SessionState.Transcribing);
    }

    [Fact]
    public void Transcribing_TranscriptReady_GoesToInjecting()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.Apply(SessionEvent.TranscriptReady);
        e.State.ShouldBe(SessionState.Injecting);
    }

    [Fact]
    public void Injecting_Done_GoesToIdle()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.Apply(SessionEvent.TranscriptReady);
        e.Apply(SessionEvent.InjectionCompleted);
        e.State.ShouldBe(SessionState.Idle);
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Transcribing)]
    [InlineData(SessionState.Injecting)]
    public void Cancel_FromAnyActiveState_GoesToIdle(SessionState start)
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        if (start == SessionState.Transcribing || start == SessionState.Injecting)
            e.Apply(SessionEvent.StopRequested);
        if (start == SessionState.Injecting)
            e.Apply(SessionEvent.TranscriptReady);

        e.State.ShouldBe(start);
        e.Apply(SessionEvent.CancelRequested);
        e.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Start_DuringRecording_IsIgnored()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StartRequested);
        e.State.ShouldBe(SessionState.Recording);
    }

    [Fact]
    public void StateChange_FiresStateChanged_WithOldAndNew()
    {
        var e = new SessionEngine();
        (SessionState From, SessionState To)? observed = null;
        e.StateChanged += (from, to) => observed = (from, to);
        e.Apply(SessionEvent.StartRequested);
        observed.ShouldBe((SessionState.Idle, SessionState.Recording));
    }
}
