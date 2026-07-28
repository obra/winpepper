namespace Winpepper.Asr.Transcription;

/// <summary>
/// One dictation's streaming transcription session. Created at recording start;
/// audio is pushed as it is captured; FinishAsync is called at recording stop.
///
/// CONTRACT: FinishAsync(fullAudio) must always return the transcript of the
/// ENTIRE dictation. Implementations that received zero pushed samples MUST
/// transcribe fullAudio from scratch, and implementations whose streaming state
/// became unusable (mid-stream failure) MUST recover internally (e.g. a batch
/// retry) — the pipeline relies on this so reliability never regresses.
/// </summary>
public interface IStreamingTranscriptionSession : IAsyncDisposable
{
    /// <summary>Feed mono 16 kHz float samples captured during recording. May do
    /// heavy work (inference / network sends) — callers pump from a background task.
    /// CONTRACT: a push arriving after DisposeAsync must be a benign no-op — the
    /// coordinator's pump legitimately drains queued frames after an abandon.</summary>
    ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);

    /// <summary>Signal end-of-audio and await the final transcript.
    /// <paramref name="fullAudio"/> is the complete (silence-trimmed) session buffer.</summary>
    Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct);
}

/// <summary>Streaming counterpart of <see cref="ITranscriber"/>: one session per dictation.</summary>
public interface IStreamingTranscriber
{
    /// <summary>The model identifier this transcriber would report on success.</summary>
    string ModelName { get; }

    Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct);
}
