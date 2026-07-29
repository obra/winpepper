namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>
/// Fake backend for CleanupBackendHolder tests: records disposal so tests can
/// prove replaced/unused backends are freed (the real LlamaCleanupBackend
/// holds native LLamaWeights).
/// </summary>
internal sealed class DisposableFakeBackend : ILlamaCleanupBackend, IDisposable
{
    private volatile bool _disposed;

    public bool Disposed => _disposed;

    public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
        => Task.FromResult("cleaned");

    public void Dispose() => _disposed = true;
}
