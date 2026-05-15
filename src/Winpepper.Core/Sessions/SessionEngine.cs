namespace Winpepper.Core.Sessions;

public sealed class SessionEngine
{
    public SessionState State { get; private set; } = SessionState.Idle;
    public event Action<SessionState, SessionState>? StateChanged;

    public void Apply(SessionEvent evt)
    {
        var from = State;
        var to = NextState(State, evt);
        if (to == State) return;
        State = to;
        StateChanged?.Invoke(from, to);
    }

    private static SessionState NextState(SessionState state, SessionEvent evt) => (state, evt) switch
    {
        (SessionState.Idle,         SessionEvent.StartRequested)       => SessionState.Recording,
        (SessionState.Recording,    SessionEvent.StopRequested)        => SessionState.Transcribing,
        (SessionState.Transcribing, SessionEvent.TranscriptReady)      => SessionState.Injecting,
        (SessionState.Injecting,    SessionEvent.InjectionCompleted)   => SessionState.Idle,

        (SessionState.Recording,    SessionEvent.CancelRequested)      => SessionState.Idle,
        (SessionState.Transcribing, SessionEvent.CancelRequested)      => SessionState.Idle,
        (SessionState.Injecting,    SessionEvent.CancelRequested)      => SessionState.Idle,

        (_,                         SessionEvent.Failed)               => SessionState.Idle,
        _                                                              => state,
    };
}
