#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>Runs after (never alongside) the parallel eval collections:
/// loading the same GGUF concurrently with the eval fixtures crashes the
/// native Vulkan loader (0xC0000005 in llama_model_load_from_file).</summary>
[CollectionDefinition("cleanup-backend-serial", DisableParallelization = true)]
public sealed class CleanupBackendSerialCollection { }

[Collection("cleanup-backend-serial")]
[Trait("Platform", "Windows")]
public class LlamaCleanupBackendIntegrationTests
{
    // Registry-derived (via CleanupEvalModels), NOT hardcoded: a previous
    // hardcoded path ('cleanup/qwen2.5-0.5b-instruct/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf')
    // silently diverged from the registry/downloader layout
    // ('cleanup/qwen2.5-0.5b-instruct-q4_k_m/qwen2.5-0.5b-instruct-q4_k_m.gguf'),
    // turning both tests into permanent skips even with the model installed.
    private static readonly string? ModelPath =
        CleanupEvalModels.CleanupModels
            .Where(d => d.Name == ModelRegistry.DefaultCleanupName)
            .Select(CleanupEvalModels.GgufPathFor)
            .FirstOrDefault();

    [Fact]
    public async Task Load_Generate_ReturnsSomething()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(ModelPath is not null && File.Exists(ModelPath),
            $"Cleanup model '{ModelRegistry.DefaultCleanupName}' not present at " +
            $"{ModelPath ?? "(no gguf in registry)"}; install it via the app's Models page");

        using var backend = new LlamaCleanupBackend(ModelPath!, new NullLogger<LlamaCleanupBackend>());
        var result = await backend.GenerateAsync(
            systemPrompt: "You repeat the user's sentence back exactly.",
            userPrompt: "Hello, world.",
            rawTranscript: "Hello, world.",
            maxNewTokens: 32,
            temperature: 0.1f,
            ct: CancellationToken.None);
        result.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Warm_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(ModelPath is not null && File.Exists(ModelPath),
            $"Cleanup model '{ModelRegistry.DefaultCleanupName}' not present at " +
            $"{ModelPath ?? "(no gguf in registry)"}; install it via the app's Models page");

        using var backend = new LlamaCleanupBackend(ModelPath!, new NullLogger<LlamaCleanupBackend>());
        await backend.WarmAsync(CancellationToken.None);
    }
}
#endif
