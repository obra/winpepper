using Winpepper.Cleanup;

namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>
/// Configurable fake LLamaSharp backend for CleanupRunner unit tests.
/// </summary>
internal sealed class FakeLlamaCleanupBackend : ILlamaCleanupBackend
{
    public string Output { get; init; } = "";
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    public Exception? Throw { get; init; }
    public int CallCount { get; private set; }
    public string? LastPrompt { get; private set; }
    public int? LastMaxNewTokens { get; private set; }

    public async Task<string> GenerateAsync(string prompt, int maxNewTokens, float temperature, CancellationToken ct)
    {
        CallCount++;
        LastPrompt = prompt;
        LastMaxNewTokens = maxNewTokens;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Throw is not null) throw Throw;
        return Output;
    }
}
