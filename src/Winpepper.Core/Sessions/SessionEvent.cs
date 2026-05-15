namespace Winpepper.Core.Sessions;

public enum SessionEvent
{
    StartRequested,
    StopRequested,
    TranscriptReady,
    InjectionCompleted,
    CancelRequested,
    Failed,
}
