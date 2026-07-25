namespace Winpepper.Core.Sessions;

public enum SessionState
{
    Idle,
    Recording,
    Transcribing,
    /// <summary>The cleanup LLM is running on this dictation's transcript.
    /// Entered ONLY when the LLM will actually run (CleanupRunner.Preflight),
    /// so the pill's "Cleaning up..." phase is truthful.</summary>
    CleaningUp,
    Injecting,
}
