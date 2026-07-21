namespace Winpepper.Asr.Transcription;

/// <summary>Result of a single dictation transcription.</summary>
/// <param name="Text">The recognized text.</param>
/// <param name="ProviderModelName">
/// Identifier of the provider/model that actually produced the text,
/// e.g. "assemblyai/universal-2" or "parakeet-tdt-0.6b-v3". Stamped onto history.
/// </param>
public sealed record TranscriptionResult(string Text, string ProviderModelName);

/// <summary>Transcribes mono 16 kHz float samples to text.</summary>
public interface ITranscriber
{
    /// <summary>The model identifier this transcriber would report on success.</summary>
    string ModelName { get; }

    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);
}
