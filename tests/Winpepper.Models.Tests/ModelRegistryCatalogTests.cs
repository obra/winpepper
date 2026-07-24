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
}
