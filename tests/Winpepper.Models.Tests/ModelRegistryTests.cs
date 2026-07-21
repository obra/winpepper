using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryTests
{
    [Fact]
    public void All_HasAtLeastOne_AsrAnd_Cleanup_Descriptor()
    {
        var registry = new ModelRegistry();
        registry.All.OfType<ModelDescriptor>().Any(d => d.Kind == ModelKind.Asr).ShouldBeTrue();
        registry.All.OfType<ModelDescriptor>().Any(d => d.Kind == ModelKind.Cleanup).ShouldBeTrue();
    }

    [Fact]
    public void Find_KnownName_ReturnsDescriptor()
    {
        var registry = new ModelRegistry();
        var d = registry.Find("parakeet-tdt-0.6b-v3");
        d.ShouldNotBeNull();
        d!.Kind.ShouldBe(ModelKind.Asr);
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        new ModelRegistry().Find("not-a-model").ShouldBeNull();
    }

    [Fact]
    public void ResolveOrDefault_UnknownAsrName_UsesCatalogDefault()
    {
        var resolved = new ModelRegistry().ResolveOrDefault("removed-asr-model", ModelKind.Asr);

        resolved.Name.ShouldBe(ModelRegistry.DefaultAsrName);
        resolved.Kind.ShouldBe(ModelKind.Asr);
    }

    [Fact]
    public void ResolveOrDefault_KnownName_PreservesSelection()
    {
        var resolved = new ModelRegistry().ResolveOrDefault(
            ModelRegistry.DefaultCleanupName, ModelKind.Cleanup);

        resolved.Name.ShouldBe(ModelRegistry.DefaultCleanupName);
        resolved.Kind.ShouldBe(ModelKind.Cleanup);
    }

    [Fact]
    public void DefaultAsrName_And_DefaultCleanupName_ResolveInRegistry()
    {
        var r = new ModelRegistry();
        r.Find(ModelRegistry.DefaultAsrName).ShouldNotBeNull();
        r.Find(ModelRegistry.DefaultCleanupName).ShouldNotBeNull();
    }

    [Fact]
    public void Every_File_Has_NonEmptyUrl_And_PositiveSize_And_64charSha()
    {
        var r = new ModelRegistry();
        foreach (var d in r.All)
        {
            foreach (var f in d.Files)
            {
                f.Url.ShouldStartWith("https://huggingface.co/");
                f.SizeBytes.ShouldBeGreaterThan(0);
                f.Sha256.Length.ShouldBe(64);
            }
        }
    }

    [Fact]
    public void ByKind_Filters_Correctly()
    {
        var r = new ModelRegistry();
        r.ByKind(ModelKind.Asr).ShouldAllBe(d => d.Kind == ModelKind.Asr);
        r.ByKind(ModelKind.Cleanup).ShouldAllBe(d => d.Kind == ModelKind.Cleanup);
    }
}
