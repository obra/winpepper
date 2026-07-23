namespace Winpepper.Cleanup;

/// <summary>
/// Abstraction over the LlamaSharp context so <see cref="CleanupRunner"/> can
/// be unit-tested without loading a real model.
/// </summary>
public interface ILlamaCleanupBackend
{
    /// <summary>
    /// Run the model with a system message (instructions/hints/OCR) and a user
    /// message (the transcript), returning the raw output. The implementation
    /// is responsible for honoring <paramref name="ct"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxNewTokens,
        float temperature,
        CancellationToken ct);
}
