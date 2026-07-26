namespace Winpepper.Asr.TranscribeCpp;

public interface ITranscribeCppStream : IDisposable
{
    /// <summary>Feed 16 kHz mono float samples. Returns the latest committed
    /// text when it changed, else null. Throws TranscribeCppException on any
    /// native error. Single-threaded.</summary>
    string? Feed(float[] samples, int count);

    /// <summary>Flush + finalize. Returns the final transcript (full_text) and
    /// the was_truncated flag. May be called with zero prior feeds.</summary>
    (string Text, bool WasTruncated) Finalize();
}

public interface ITranscribeCppEngine : IDisposable
{
    string ModelName { get; }
    /// <summary>Begin one streaming session (one per dictation). Acquires the
    /// engine-wide compute gate for the STREAM'S LIFETIME (released when the
    /// stream is disposed) — transcribe.cpp 0.x allows at most one compute in
    /// flight per model (see Global Constraints). Throws TranscribeCppException
    /// if the gate cannot be acquired within 5 s (previous dictation's stream
    /// not yet disposed) — callers fall back to batch.
    /// attContextRight in encoder frames: {13,6,1,0} = {1040,480,80,0} ms.</summary>
    ITranscribeCppStream BeginStream(int attContextRight);
    /// <summary>Offline single-utterance transcription on a dedicated native
    /// session (bench parity reference; not used by the app pipeline). Holds
    /// the same compute gate for the duration of the call.</summary>
    string TranscribeBatch(float[] mono16k);
}
