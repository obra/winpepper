using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class DebouncedSettingsWriterDiffTests
{
    [Fact]
    public void PropertyValuesEqual_StringSequences_CompareByContent()
    {
        // A freshly deserialized list must not mis-diff as "changed" just
        // because it is a different instance.
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a", "b" }, new List<string> { "a", "b" }).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a" }, new[] { "b" }).ShouldBeFalse();
        DebouncedSettingsWriter.PropertyValuesEqual(
            new[] { "a", "b" }, new[] { "b", "a" }).ShouldBeFalse(); // order matters (it is a ladder)
    }

    [Fact]
    public void PropertyValuesEqual_Scalars_UseEquals()
    {
        DebouncedSettingsWriter.PropertyValuesEqual(5, 5).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual("x", "x").ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(null, null).ShouldBeTrue();
        DebouncedSettingsWriter.PropertyValuesEqual(5, 6).ShouldBeFalse();
        DebouncedSettingsWriter.PropertyValuesEqual("x", null).ShouldBeFalse();
    }
}
