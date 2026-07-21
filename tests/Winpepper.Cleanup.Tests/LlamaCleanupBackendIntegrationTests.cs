#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

[Trait("Platform", "Windows")]
public class LlamaCleanupBackendIntegrationTests
{
    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winpepper", "models", "cleanup", "qwen2.5-0.5b-instruct",
        "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf");

    [Fact]
    public async Task Load_Generate_ReturnsSomething()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(File.Exists(ModelPath),
            $"Cleanup model not present at {ModelPath}; run scripts/download-cleanup-model.ps1");

        using var backend = new LlamaCleanupBackend(ModelPath, new NullLogger<LlamaCleanupBackend>());
        var result = await backend.GenerateAsync(
            systemPrompt: "You repeat the user's sentence back exactly.",
            userPrompt: "Hello, world.",
            maxNewTokens: 32,
            temperature: 0.1f,
            ct: CancellationToken.None);
        result.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Warm_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(File.Exists(ModelPath),
            $"Cleanup model not present at {ModelPath}; run scripts/download-cleanup-model.ps1");

        using var backend = new LlamaCleanupBackend(ModelPath, new NullLogger<LlamaCleanupBackend>());
        await backend.WarmAsync(CancellationToken.None);
    }
}
#endif
