namespace Winpepper.Cleanup;

/// <summary>
/// Abstraction over the LlamaSharp context so <see cref="CleanupRunner"/> can
/// be unit-tested without loading a real model.
/// </summary>
public interface ILlamaCleanupBackend
{
    /// <summary>
    /// Run the model with a system message (instructions/hints/OCR) and a user
    /// message (the &lt;USER-INPUT&gt;-wrapped transcript), returning the raw
    /// output. <paramref name="rawTranscript"/> is the unwrapped transcript:
    /// raw-completion prompt formats (see
    /// <see cref="CleanupPromptFormatter.RawIo"/>) feed the model ONLY that
    /// text; instruction formats ignore it. The implementation is responsible
    /// for honoring <paramref name="ct"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        string rawTranscript,
        int maxNewTokens,
        float temperature,
        CancellationToken ct);
}
