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
    /// attContextRight in encoder frames: {13,6,1,0} = {1040,480,80,0} ms.
    /// language: optional source-language hint (BCP-47-ish, e.g. "en-US");
    /// null = model default/autodetect.
    /// B4 (gateWaitMs): how long THIS call spent waiting on the
    /// compute gate before any native work started, returned PER CALL — never
    /// a shared mutable slot: calls on the singleton engine can overlap (a
    /// cancel-orphaned pump's wedged BeginStream vs the next dictation's), so
    /// a read-after-call property would mis-attribute another call's gate
    /// wait. The engine writes this immediately after the gate wait completes
    /// and BEFORE the gate-timeout throw; `out` is by-ref, so the value is
    /// valid to the caller on both return and throw. Lets the caller book
    /// queueing behind a prior stream separately from native compute.</summary>
    ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs);
    /// <summary>Offline single-utterance transcription on a dedicated native
    /// session (bench parity reference; not used by the app pipeline). Holds
    /// the same compute gate for the duration of the call.
    /// language: optional source-language hint (BCP-47-ish, e.g. "en-US");
    /// null = model default/autodetect.
    /// gateWaitMs: this call's compute-gate wait, per-call (see BeginStream).</summary>
    string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs);
}
