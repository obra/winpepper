using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class CleanupModelPathResolverTests
{
    private static readonly string Root = Path.Combine("C:", "root", "models");

    [Fact]
    public void Resolve_ExactKey_ReturnsThatModelsGgufPath()
    {
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(
            registry, Root, "qwen2.5-0.5b-instruct-q4_k_m");

        resolution.GgufPath.ShouldBe(Path.Combine(
            Root, "cleanup", "qwen2.5-0.5b-instruct-q4_k_m", "qwen2.5-0.5b-instruct-q4_k_m.gguf"));
        resolution.ResolvedName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        resolution.FellBackToDefault.ShouldBeFalse();
        resolution.PromptFormat.ShouldBe("chatml");
    }

    [Theory]
    [InlineData("qwen2.5-0.5b-instruct-q4_k_m", "chatml")]
    [InlineData("lfm2.5-1.2b-instruct-q4_k_m", "chatml")]
    [InlineData("granite-4.0-1b-q4_k_m", "granite")]
    [InlineData("sotto-cleanup-lfm25-350m-q8_0", "raw-io")]
    public void Resolve_CarriesTheDescriptorsPromptFormat(string name, string expectedFormat)
    {
        var resolution = CleanupModelPathResolver.Resolve(new ModelRegistry(), Root, name);

        resolution.ResolvedName.ShouldBe(name);
        resolution.PromptFormat.ShouldBe(expectedFormat);
        resolution.GgufPath.ShouldNotBeNull();
    }

    [Fact]
    public void Resolve_NullName_ResolvesKindDefault_WithoutFallbackFlag()
    {
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(registry, Root, null);

        resolution.ResolvedName.ShouldBe(ModelRegistry.DefaultCleanupName);
        resolution.GgufPath.ShouldNotBeNull();
        resolution.FellBackToDefault.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_EmptyName_ResolvesKindDefault_WithoutFallbackFlag()
    {
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(registry, Root, "");

        resolution.ResolvedName.ShouldBe(ModelRegistry.DefaultCleanupName);
        resolution.FellBackToDefault.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_UnknownName_FallsBackToDefault_AndFlagsIt()
    {
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(registry, Root, "no-such-model");

        resolution.ResolvedName.ShouldBe(ModelRegistry.DefaultCleanupName);
        resolution.GgufPath.ShouldBe(Path.Combine(
            Root, "cleanup", "qwen2.5-0.5b-instruct-q4_k_m", "qwen2.5-0.5b-instruct-q4_k_m.gguf"));
        resolution.FellBackToDefault.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_NameOfWrongKind_FallsBackToDefault_AndFlagsIt()
    {
        // An ASR model name is "known" to the registry but is not a cleanup
        // model, so ResolveOrDefault falls back to the cleanup default.
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(
            registry, Root, ModelRegistry.DefaultAsrName);

        resolution.ResolvedName.ShouldBe(ModelRegistry.DefaultCleanupName);
        resolution.FellBackToDefault.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_PathIsBuiltWithPlatformSeparators()
    {
        var registry = new ModelRegistry();

        var resolution = CleanupModelPathResolver.Resolve(
            registry, Root, ModelRegistry.DefaultCleanupName);

        // No hardcoded separators: the path must decompose exactly into the
        // Path.Combine of its segments on the current platform.
        resolution.GgufPath.ShouldBe(Path.Combine(
            Path.Combine("C:", "root", "models"),
            Path.Combine("cleanup", "qwen2.5-0.5b-instruct-q4_k_m"),
            "qwen2.5-0.5b-instruct-q4_k_m.gguf"));
    }
}
