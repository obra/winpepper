namespace Winpepper.History.Lab;

/// <summary>
/// Reruns a locally installed speech model (or a fake) over an existing WAV
/// file and returns the transcript. Stateless from the caller's perspective —
/// each call resolves the requested model and transcribes independently.
/// </summary>
public interface ITranscriptionRerunService
{
    Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct);
}
