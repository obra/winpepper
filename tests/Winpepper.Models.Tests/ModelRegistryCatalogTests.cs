using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryCatalogTests
{
    [Fact]
    public void ByKind_Asr_ExposesAtLeastTwoLocalDescriptors()
    {
        var registry = new ModelRegistry();

        var asr = registry.ByKind(ModelKind.Asr).ToList();

        // The live-swap Swap branch is only reachable with >=2 local ASR models.
        asr.Count.ShouldBeGreaterThanOrEqualTo(2);
        asr.Select(d => d.Name).Distinct().Count().ShouldBe(asr.Count);
        asr.ShouldContain(d => d.Name == ModelRegistry.DefaultAsrName);
        asr.ShouldContain(d => d.Name == ModelRegistry.SecondAsrName);
    }

    [Fact]
    public void ByKind_Asr_LocalNamesNeverMatchCloudProviderPrefix()
    {
        var registry = new ModelRegistry();

        foreach (var d in registry.ByKind(ModelKind.Asr))
        {
            // CloudProvider.IsCloud (CloudProvider.cs:12-14) is an "assemblyai/"
            // prefix check; a local catalog name must never satisfy it, or the
            // history pipeline would treat a local dictation as cloud.
            d.Name.ShouldNotStartWith("assemblyai/");
        }
    }

    [Fact]
    public void Registry_contains_the_nemotron_streaming_model()
    {
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName);
        Assert.NotNull(d);
        Assert.Equal(ModelKind.StreamingAsr, d!.Kind);
        Assert.Equal("nemotron-streaming-en", d.InstallDirRelative);
        Assert.Equal(2, d.Files.Count);

        var gguf = d.Files.Single(f => f.RelativePath.EndsWith(".gguf"));
        Assert.Equal("nemotron-speech-streaming-en-0.6b-Q8_0.gguf", gguf.RelativePath);
        Assert.Equal(729_650_176, gguf.SizeBytes);
        Assert.Equal("90d8c89714cd31efc88be62a40c6b2bea57e0cc2063af1ffe2c28f1a228ca110", gguf.Sha256);
        Assert.Null(gguf.ExtractToRelative);

        var runtime = d.Files.Single(f => f.RelativePath.EndsWith(".tar.gz"));
        Assert.Equal(25_957_910, runtime.SizeBytes);
        Assert.Equal("9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5", runtime.Sha256);
        Assert.Equal("runtime", runtime.ExtractToRelative);
        Assert.StartsWith("https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/", runtime.Url);
    }

    [Fact]
    public void ByKind_Cleanup_ExposesFourModels_WithQwenStillTheDefault()
    {
        var registry = new ModelRegistry();

        var cleanup = registry.ByKind(ModelKind.Cleanup).ToList();

        cleanup.Select(d => d.Name).ShouldBe(new[]
        {
            "qwen2.5-0.5b-instruct-q4_k_m",
            "lfm2.5-1.2b-instruct-q4_k_m",
            "granite-4.0-1b-q4_k_m",
            "sotto-cleanup-lfm25-350m-q8_0",
        });
        ModelRegistry.DefaultCleanupName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        registry.ResolveOrDefault(null, ModelKind.Cleanup).Name
            .ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void CleanupModels_DeclareTheirVerifiedPromptFormats()
    {
        var registry = new ModelRegistry();

        registry.Find("qwen2.5-0.5b-instruct-q4_k_m")!.PromptFormat.ShouldBe("chatml");
        registry.Find("lfm2.5-1.2b-instruct-q4_k_m")!.PromptFormat.ShouldBe("chatml");
        registry.Find("granite-4.0-1b-q4_k_m")!.PromptFormat.ShouldBe("granite");
        registry.Find("sotto-cleanup-lfm25-350m-q8_0")!.PromptFormat.ShouldBe("raw-io");
    }

    [Fact]
    public void Registry_contains_the_lfm25_cleanup_model()
    {
        var d = new ModelRegistry().Find("lfm2.5-1.2b-instruct-q4_k_m");

        Assert.NotNull(d);
        Assert.Equal(ModelKind.Cleanup, d!.Kind);
        Assert.Equal("LFM2.5 1.2B Instruct (Q4_K_M GGUF)", d.DisplayName);
        Assert.Equal(Path.Combine("cleanup", "lfm2.5-1.2b-instruct-q4_k_m"), d.InstallDirRelative);
        Assert.False(d.ManualInstallOnly);

        var gguf = d.Files.Single();
        Assert.Equal("LFM2.5-1.2B-Instruct-Q4_K_M.gguf", gguf.RelativePath);
        Assert.Equal("https://huggingface.co/LiquidAI/LFM2.5-1.2B-Instruct-GGUF/resolve/main/LFM2.5-1.2B-Instruct-Q4_K_M.gguf", gguf.Url);
        Assert.Equal("b1b3de114215d9507409a662a501a631095a479a419584e8a2ded6304b19b4f5", gguf.Sha256);
        Assert.Equal(730_895_168, gguf.SizeBytes);
    }

    [Fact]
    public void Registry_contains_the_granite_cleanup_model()
    {
        var d = new ModelRegistry().Find("granite-4.0-1b-q4_k_m");

        Assert.NotNull(d);
        Assert.Equal(ModelKind.Cleanup, d!.Kind);
        Assert.Equal("Granite 4.0 1B (Q4_K_M GGUF)", d.DisplayName);
        Assert.Equal(Path.Combine("cleanup", "granite-4.0-1b-q4_k_m"), d.InstallDirRelative);
        Assert.False(d.ManualInstallOnly);

        var gguf = d.Files.Single();
        Assert.Equal("granite-4.0-1b-Q4_K_M.gguf", gguf.RelativePath);
        Assert.Equal("https://huggingface.co/ibm-granite/granite-4.0-1b-GGUF/resolve/main/granite-4.0-1b-Q4_K_M.gguf", gguf.Url);
        Assert.Equal("22ec0f9cc99a90185312de3c882c84e7bd6789bdd050389844380a01a831d7f1", gguf.Sha256);
        Assert.Equal(1_023_645_440, gguf.SizeBytes);
    }

    [Fact]
    public void Registry_contains_the_sotto_cleanup_model_AsManualInstallOnly()
    {
        var d = new ModelRegistry().Find("sotto-cleanup-lfm25-350m-q8_0");

        Assert.NotNull(d);
        Assert.Equal(ModelKind.Cleanup, d!.Kind);
        Assert.Equal("Sotto Cleanup LFM2.5 350M (Q8_0 GGUF)", d.DisplayName);
        Assert.Equal(Path.Combine("cleanup", "sotto-cleanup-lfm25-350m-q8_0"), d.InstallDirRelative);
        // No public GGUF exists (converted locally): never downloadable.
        Assert.True(d.ManualInstallOnly);

        var gguf = d.Files.Single();
        Assert.Equal("sotto-cleanup-lfm25-350m-q8_0.gguf", gguf.RelativePath);
        Assert.Equal("", gguf.Url);
        // Values of the locally converted GGUF (convert_hf_to_gguf.py --outtype q8_0,
        // source juanquivilla/sotto-cleanup-lfm25-350m, 2026-07-27).
        Assert.Equal("67113c655d523ea682ff30488900fb62415835d391ce77cd1cb97dff2f5d962d", gguf.Sha256);
        Assert.Equal(379215808, gguf.SizeBytes);
    }

    [Fact]
    public void OnlyManualInstallDescriptors_MayHaveAnEmptyDownloadUrl()
    {
        foreach (var d in new ModelRegistry().All)
        {
            foreach (var f in d.Files)
            {
                if (d.ManualInstallOnly) continue;
                f.Url.ShouldStartWith("https://");
            }
        }
    }

    [Fact]
    public void StreamingAsr_kind_never_appears_in_the_batch_asr_list()
        => Assert.DoesNotContain(new ModelRegistry().ByKind(ModelKind.Asr),
            d => d.Kind == ModelKind.StreamingAsr);

    [Fact]
    public void ResolveOrDefault_throws_for_StreamingAsr_kind_defaults()
        // StreamingAsr deliberately has no default: it is never a resolvable
        // AsrModelName (it auto-installs in the background but is not a batch
        // ASR selection). This test documents that contract.
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ModelRegistry().ResolveOrDefault(null, ModelKind.StreamingAsr));
}
