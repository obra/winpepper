namespace Winpepper.Cleanup;

/// <summary>
/// Abstraction over the LlamaSharp context so <see cref="CleanupRunner"/> can
/// be unit-tested without loading a real model.
/// </summary>
public interface ILlamaCleanupBackend
{
    /// <summary>
    /// Run the model on the assembled prompt and return the raw output. The
    /// implementation is responsible for honoring <paramref name="ct"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string prompt,
        int maxNewTokens,
        float temperature,
        CancellationToken ct);
}
