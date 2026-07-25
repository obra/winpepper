namespace Winpepper.Core.Sessions;

public enum SessionEvent
{
    StartRequested,
    StopRequested,
    /// <summary>Transcript is ready and NO cleanup LLM will run — go straight to Injecting.</summary>
    TranscriptReady,
    /// <summary>Transcript is ready and the cleanup LLM WILL run (CleanupRunner.Preflight true).</summary>
    CleanupStarted,
    /// <summary>The cleanup attempt finished (success or fallback) — proceed to Injecting.</summary>
    CleanupCompleted,
    InjectionCompleted,
    CancelRequested,
    Failed,
}
