using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiModelsTests
{
    [Fact]
    public void Known_ListsLatestThenFast_InOrder()
    {
        AssemblyAiModels.Known.Select(m => m.Id)
            .ShouldBe(new[] { "universal-3-5-pro", "universal-2" });
        AssemblyAiModels.Known[0].Label.ShouldBe("Universal-3.5 Pro - latest, most accurate");
        AssemblyAiModels.Known[1].Label.ShouldBe("Universal-2 - faster, lower cost");
    }

    [Theory]
    [InlineData("universal-3-5-pro", true)]   // canonical/listed premium id
    [InlineData("universal-2", true)]         // listed fast id
    [InlineData("UNIVERSAL-3-PRO", true)]     // pricing-page spelling, case-insensitive
    [InlineData("best", true)]                // deprecated AssemblyAI alias
    [InlineData("NANO", true)]                // deprecated alias, case-insensitive
    [InlineData("universal-9000", false)]     // typo -> not known
    [InlineData("", false)]
    public void IsKnown_RecognizesGoodIds(string id, bool expected)
        => AssemblyAiModels.IsKnown(id).ShouldBe(expected);

    [Fact]
    public void DefaultId_IsUniversal35Pro()
        => AssemblyAiModels.DefaultId.ShouldBe("universal-3-5-pro");

    [Theory]
    [InlineData("universal-3-pro", "universal-3-5-pro")]   // pricing alias -> canonical
    [InlineData("UNIVERSAL-3-PRO", "universal-3-5-pro")]   // case-insensitive
    [InlineData("best", "universal-3-5-pro")]              // deprecated alias -> premium
    [InlineData("nano", "universal-2")]                    // deprecated alias -> fast
    [InlineData("universal-3-5-pro", "universal-3-5-pro")] // already-listed id -> unchanged
    [InlineData("universal-2", "universal-2")]             // already-listed id -> unchanged
    [InlineData("my-custom-model", "my-custom-model")]     // custom id -> unchanged
    [InlineData("", "")]                                   // empty -> unchanged
    public void CanonicalId_MapsAliasesToListedIds(string id, string expected)
        => AssemblyAiModels.CanonicalId(id).ShouldBe(expected);

    // Crash guard (spec): the settings-page model combo canonicalizes a stored
    // value and then selects the matching combo item. A stored value that is now
    // an alias (e.g. "universal-3-pro") MUST canonicalize to an id present in
    // Known so the combo selects a real listed item instead of mis-selecting or
    // dropping to the custom escape hatch. This is the pure coverage of the
    // combo-selection logic that previously crashed the settings page.
    [Theory]
    [InlineData("universal-3-pro")]
    [InlineData("best")]
    [InlineData("nano")]
    [InlineData("universal-3-5-pro")]
    [InlineData("universal-2")]
    public void CanonicalId_EveryKnownAliasOrId_ResolvesToAListedModelId(string id)
    {
        var canonical = AssemblyAiModels.CanonicalId(id);
        AssemblyAiModels.Known
            .Any(m => string.Equals(m.Id, canonical, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();
    }
}
