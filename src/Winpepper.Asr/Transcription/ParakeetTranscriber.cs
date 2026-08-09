namespace Winpepper.Asr.Transcription;

/// <summary>Adapts the local ONNX Parakeet session to the ITranscriber seam.
/// With ownsSession=true (PipelineHost's loader) disposing the transcriber
/// disposes the underlying session; the default false preserves the legacy
/// borrow semantics for any other call site.</summary>
public sealed class ParakeetTranscriber : IDisposableTranscriber
{
    private readonly ParakeetSession _session;
    private readonly bool _ownsSession;

    public ParakeetTranscriber(ParakeetSession session, string modelName, bool ownsSession = false)
    {
        _session = session;
        ModelName = modelName;
        _ownsSession = ownsSession;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            var transcript = _session.Transcribe(mono16k.Span);
            return new TranscriptionResult(transcript.Text, ModelName);
        }, ct);

    public void Dispose()
    {
        if (_ownsSession) _session.Dispose();
    }
}
