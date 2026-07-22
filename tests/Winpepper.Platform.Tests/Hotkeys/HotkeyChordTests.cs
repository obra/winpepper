using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyChordTests
{
    [Theory]
    [InlineData("RightCtrl+RightShift", Modifier.RightCtrl | Modifier.RightShift, 0)]
    [InlineData("Ctrl+Shift+Space", Modifier.Ctrl | Modifier.Shift, 0x20)]
    [InlineData("Esc", Modifier.None, 0x1B)]
    [InlineData("LeftAlt+F12", Modifier.LeftAlt, 0x7B)]
    [InlineData("F24", Modifier.None, 0x87)]
    [InlineData("Application", Modifier.None, 0x5D)]
    [InlineData("Alt+Win", Modifier.Alt | Modifier.Win, 0)]
    [InlineData("Copilot", Modifier.LeftShift | Modifier.LeftWin, 0x86)]
    public void Parse_ValidStrings_ProducesExpectedChord(string text, Modifier mods, int vk)
    {
        var chord = HotkeyChord.Parse(text);
        chord.Modifiers.ShouldBe(mods);
        chord.VirtualKey.ShouldBe(vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("+Shift")]
    [InlineData("NotARealKey")]
    public void Parse_Invalid_ThrowsFormatException(string text)
    {
        Should.Throw<FormatException>(() => HotkeyChord.Parse(text));
    }

    [Fact]
    public void Matches_IgnoresExtraModifiers_WhenChordHasNoModifierRequirement()
    {
        // "Esc" with no required modifiers matches regardless of what's down.
        var chord = HotkeyChord.Parse("Esc");
        chord.Matches(0x1B, Modifier.LeftCtrl).ShouldBeTrue();
        chord.Matches(0x20, Modifier.None).ShouldBeFalse();
    }

    [Fact]
    public void Matches_RequiresExactModifierSet_WhenSpecified()
    {
        var chord = HotkeyChord.Parse("RightCtrl+RightShift");
        chord.Matches(0, Modifier.RightCtrl | Modifier.RightShift).ShouldBeTrue();
        chord.Matches(0, Modifier.RightCtrl).ShouldBeFalse();
        chord.Matches(0, Modifier.RightCtrl | Modifier.RightShift | Modifier.LeftCtrl).ShouldBeFalse();
    }

    [Fact]
    public void Matches_RejectsExtraModifierGroup()
    {
        var chord = HotkeyChord.Parse("LeftCtrl+LeftShift");

        chord.Matches(0, Modifier.LeftCtrl | Modifier.LeftShift | Modifier.LeftAlt).ShouldBeFalse();
        chord.Matches(0, Modifier.LeftCtrl | Modifier.LeftShift | Modifier.RightWin).ShouldBeFalse();
    }

    [Fact]
    public void ToString_RoundTrips_Through_Parse()
    {
        var original = HotkeyChord.Parse("Ctrl+Shift+Space");
        var formatted = original.ToString();
        var roundtripped = HotkeyChord.Parse(formatted);
        roundtripped.ShouldBe(original);
    }

    [Theory]
    [InlineData("Menu")]
    [InlineData("Apps")]
    [InlineData("ContextMenu")]
    public void ApplicationAliases_ParseAndFormatCanonically(string alias)
        => HotkeyChord.Parse(alias).ToString().ShouldBe("Application");

    [Fact]
    public void Copilot_FormatsCanonicalHardwareSequence()
    {
        HotkeyChord.Parse("LeftShift+LeftWin+F23").ToString().ShouldBe("Copilot");
        HotkeyChord.Parse("Copilot").ShouldBe(HotkeyChord.Parse("LeftShift+LeftWin+F23"));
    }

    [Theory]
    [InlineData("F13")]
    [InlineData("F24")]
    [InlineData("Application")]
    [InlineData("Copilot")]
    [InlineData("Alt+Win")]
    public void SupportedChord_RoundTrips(string text)
    {
        var chord = HotkeyChord.Parse(text);
        HotkeyChord.Parse(chord.ToString()).ShouldBe(chord);
    }
}
