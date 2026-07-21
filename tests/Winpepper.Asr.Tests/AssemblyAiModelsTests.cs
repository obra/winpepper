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
}
