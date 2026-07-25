namespace Winpepper.Core.Errors;

/// <summary>
/// Marks an <see cref="ErrorStage.Audio"/> report as the ONGOING "capture is
/// down" CONDITION rather than a per-dictation Audio EVENT (the "no audio
/// detected" report from a finished session). Both arrive at the same stage, so
/// the stage alone cannot distinguish them; the capture-fault site wraps its
/// exception in this type and <see cref="ErrorClassifier"/> keys on it.
///
/// The inner exception's message is preserved verbatim so the Diagnostics page
/// and tray tooltip read exactly as before.
/// </summary>
public sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(Exception inner)
        : base(inner?.Message ?? "Microphone unavailable.", inner)
    {
    }
}
