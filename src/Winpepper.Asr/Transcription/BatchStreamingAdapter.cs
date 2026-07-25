namespace Winpepper.Asr.Transcription;

/// <summary>
/// Adapts a batch <see cref="ITranscriber"/> to the streaming seam. Pushed audio
/// is ignored — the pipeline hands the authoritative full buffer to FinishAsync —
/// so this adapter preserves batch behavior exactly. Used when a provider has no
/// streaming implementation and as the stop-time fallback path.
/// </summary>
public sealed class BatchStreamingAdapter : IStreamingTranscriber
{
    private readonly ITranscriber _inner;

    public BatchStreamingAdapter(ITranscriber inner) => _inner = inner;

    public string ModelName => _inner.ModelName;

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(new Session(_inner));

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly ITranscriber _inner;
        internal Session(ITranscriber inner) => _inner = inner;

        public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            => ValueTask.CompletedTask;

        public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
            => _inner.TranscribeAsync(fullAudio, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
