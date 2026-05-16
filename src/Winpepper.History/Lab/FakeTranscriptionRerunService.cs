namespace Winpepper.History.Lab;

/// <summary>
/// Returns canned transcripts so cross-platform tests can exercise the
/// Lab view-model without loading ORT.
/// </summary>
public sealed class FakeTranscriptionRerunService : ITranscriptionRerunService
{
    private readonly Func<string, string, string> _produce;
    public FakeTranscriptionRerunService(Func<string, string, string>? produce = null)
        => _produce = produce ?? ((_, modelName) => $"[fake {modelName}]");

    public Task<TranscriptionRerunResult> RerunAsync(string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(wavPath)) throw new FileNotFoundException(wavPath);
        return Task.FromResult(new TranscriptionRerunResult
        {
            ModelName = modelName,
            Text = _produce(wavPath, modelName),
            Elapsed = TimeSpan.Zero,
        });
    }
}
