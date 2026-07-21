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

    /// <summary>Begin a session, seeding up to <paramref name="includePrerollMs"/>
    /// milliseconds of already-captured audio.</summary>
    void StartSession(int includePrerollMs);

    /// <summary>End the session and return pre-roll + live audio (mono 16 kHz).</summary>
    float[] StopSession();
}
