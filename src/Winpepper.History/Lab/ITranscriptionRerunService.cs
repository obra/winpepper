namespace Winpepper.History.Lab;

/// <summary>
/// Runs Parakeet (or a fake) over an existing WAV file and returns the
/// transcript. Stateless from the caller's perspective — every call constructs
/// a fresh session against the supplied model directory.
/// </summary>
public interface ITranscriptionRerunService
{
    Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct);
}
