using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiModelsTests
{
    [Fact]
    public void Known_ListsFastAndPremium_InOrder()
    {
        AssemblyAiModels.Known.Select(m => m.Id).ShouldBe(new[] { "universal-2", "universal-3-pro" });
        AssemblyAiModels.Known[0].Label.ShouldContain("fast");
        AssemblyAiModels.Known[1].Label.ShouldContain("premium");
    }

    [Theory]
    [InlineData("universal-2", true)]
    [InlineData("UNIVERSAL-3-PRO", true)]     // case-insensitive
    [InlineData("universal-3-5-pro", true)]   // API-reference spelling accepted as alias
    [InlineData("universal-9000", false)]     // typo -> not known
    [InlineData("", false)]
    public void IsKnown_RecognizesGoodIds(string id, bool expected)
        => AssemblyAiModels.IsKnown(id).ShouldBe(expected);

    [Fact]
    public void DefaultId_IsUniversal2()
        => AssemblyAiModels.DefaultId.ShouldBe("universal-2");

    [Theory]
    [InlineData("universal-3-5-pro", "universal-3-pro")]  // alias -> listed premium id
    [InlineData("UNIVERSAL-3-5-PRO", "universal-3-pro")]  // alias mapping is case-insensitive
    [InlineData("universal-2", "universal-2")]            // already-listed id -> unchanged
    [InlineData("universal-3-pro", "universal-3-pro")]    // already-listed id -> unchanged
    [InlineData("my-custom-model", "my-custom-model")]    // custom id -> unchanged
    [InlineData("", "")]                                    // empty -> unchanged
    public void CanonicalId_MapsAliasesToListedIds(string id, string expected)
        => AssemblyAiModels.CanonicalId(id).ShouldBe(expected);

    [Fact]
    public void CanonicalId_ResolvesAlias_ToAListedModelId()
    {
        // Guards the picker: the accepted alias must canonicalize to an id that exists
        // in Known, so the model combo can always select a real item instead of
        // throwing on a recognized-but-unlisted alias.
        var canonical = AssemblyAiModels.CanonicalId("universal-3-5-pro");
        AssemblyAiModels.Known.Any(m => string.Equals(m.Id, canonical, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();
    }
}
