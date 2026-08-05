using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class SelectedModelsPolicyTests
{
    private static SelectedModelsPolicy.SelectedModel Model(
        string name, bool installed, bool manual = false) => new(name, installed, manual);

    [Fact]
    public void BuildSelection_Includes_Asr_Streaming_And_Cleanup_When_Cleanup_Enabled()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: true);

        selection.Count.ShouldBe(3);
        selection[0].Name.ShouldBe("asr-a");
        selection[1].Name.ShouldBe("stream-a");
        selection[2].Name.ShouldBe("clean-a");
    }

    [Fact]
    public void BuildSelection_Excludes_Cleanup_When_Cleanup_Disabled()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: false);

        selection.ShouldAllBe(m => m.Name != "clean-a");
        selection.Count.ShouldBe(2);
    }

    [Fact]
    public void BuildSelection_Skips_Null_Slots()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: null, streaming: null, cleanup: null, cleanupEnabled: true);

        selection.ShouldBeEmpty();
    }

    [Fact]
    public void DownloadableMissingNames_Returns_Only_Missing_Downloadable_Models()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "stream-a", "clean-a" });
    }

    [Fact]
    public void DownloadableMissingNames_Excludes_Manual_Install_Only_Models()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: false),
            streaming: null,
            cleanup: Model("sotto", installed: false, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "asr-a" });
    }

    [Fact]
    public void DownloadableMissingNames_Deduplicates_Repeated_Names()
    {
        // Two dropdowns pointing at the same registry entry must not download it twice.
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("same", installed: false),
            streaming: Model("same", installed: false),
            cleanup: null,
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "same" });
    }

    [Fact]
    public void ManualOnlyMissingNames_Returns_Manual_Models_That_Are_Missing()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: null,
            cleanup: Model("sotto", installed: false, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.ManualOnlyMissingNames(selection)
            .ShouldBe(new[] { "sotto" });
    }

    [Fact]
    public void ManualOnlyMissingNames_Excludes_Installed_Manual_Models()
    {
        // An installed manual model needs no note and no download.
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: null,
            cleanup: Model("sotto", installed: true, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.ManualOnlyMissingNames(selection).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false, true)]  // missing + downloadable => enabled
    [InlineData(true, false, false)]  // installed => nothing to do
    [InlineData(false, true, false)]  // missing but manual-only => button cannot help
    [InlineData(true, true, false)]   // installed manual model => nothing to do
    public void DownloadButtonEnabled_Truth_Table(bool installed, bool manual, bool expected)
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed, manual),
            streaming: null, cleanup: null, cleanupEnabled: true);

        SelectedModelsPolicy.DownloadButtonEnabled(selection).ShouldBe(expected);
    }

    [Fact]
    public void DownloadButtonEnabled_Is_False_For_Empty_Selection() =>
        SelectedModelsPolicy.DownloadButtonEnabled([]).ShouldBeFalse();

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Cleanup_Gate_Mirrors_The_Setting(bool cleanupEnabled, bool cardEnabled, bool noteVisible)
    {
        SelectedModelsPolicy.CleanupCardEnabled(cleanupEnabled).ShouldBe(cardEnabled);
        SelectedModelsPolicy.CleanupOffNoteVisible(cleanupEnabled).ShouldBe(noteVisible);
    }
}
