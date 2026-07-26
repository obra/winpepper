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
