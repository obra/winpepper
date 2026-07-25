namespace Winpepper.Audio;

/// <summary>
/// A capture that can start a dictation session with a pre-roll of audio that
/// was already flowing before the session began (Bug 2). Frames are raised only
/// while a session is active, so the voice meter is quiet at idle.
/// </summary>
public interface IWarmAudioRecorder : IDisposable
{
    /// <summary>Raised (mono 16 kHz frames) only while a session is active.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when the capture stream faults or fails to (re)start, so
    /// the host can log it and surface a user-facing signal (Bug 3).</summary>
    event Action<Exception>? CaptureFaulted;

    /// <summary>
    /// Raised when capture is proven healthy again after a fault - i.e. a
    /// rebuild actually succeeded. This is the RECOVERY SUCCESS that clears the
    /// microphone CONDITION; nothing else may clear it, and never a timer.
    /// MAY be raised more than once for a single failing episode (the frame
    /// path and the fault-handler reconcile can both fire - see Task 6's
    /// ordering invariant), so subscribers MUST be idempotent. Clearing an
    /// already-cleared condition is a no-op at the view model.
    /// </summary>
    event Action? CaptureRecovered;

    /// <summary>Begin a session, seeding up to <paramref name="includePrerollMs"/>
    /// milliseconds of already-captured audio.</summary>
    void StartSession(int includePrerollMs);

    /// <summary>End the session and return pre-roll + live audio (mono 16 kHz).</summary>
    float[] StopSession();
}
