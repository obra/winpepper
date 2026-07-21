namespace Winpepper.Asr.Transcription;

/// <summary>Adapts the local ONNX Parakeet session to the ITranscriber seam.</summary>
public sealed class ParakeetTranscriber : ITranscriber
{
    private readonly ParakeetSession _session;

    public ParakeetTranscriber(ParakeetSession session, string modelName)
    {
        _session = session;
        ModelName = modelName;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            var transcript = _session.Transcribe(mono16k.Span);
            return new TranscriptionResult(transcript.Text, ModelName);
        }, ct);
}
