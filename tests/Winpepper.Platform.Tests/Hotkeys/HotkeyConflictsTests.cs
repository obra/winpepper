using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyConflictsTests
{
    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+Z")]
    [InlineData("Alt+F4")]
    [InlineData("Win+L")]
    [InlineData("Win+D")]
    [InlineData("Win+E")]
    public void Common_Shortcuts_Are_Flagged(string chord)
    {
        var c = HotkeyChord.Parse(chord);
        HotkeyConflicts.Describe(c).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("RightCtrl+RightShift")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("RightAlt+F12")]
    public void Dictation_Defaults_Are_Not_Flagged(string chord)
    {
        var c = HotkeyChord.Parse(chord);
        HotkeyConflicts.Describe(c).ShouldBeNull();
    }

    [Fact]
    public void Same_Chord_For_Hold_And_Toggle_Is_A_Conflict()
    {
        var hold = HotkeyChord.Parse("RightCtrl+RightShift");
        var toggle = HotkeyChord.Parse("RightCtrl+RightShift");
        HotkeyConflicts.HoldAndToggleClash(hold, toggle).ShouldBeTrue();
    }

    [Fact]
    public void Different_Chords_Do_Not_Clash()
    {
        var hold = HotkeyChord.Parse("RightCtrl+RightShift");
        var toggle = HotkeyChord.Parse("Ctrl+Shift+Space");
        HotkeyConflicts.HoldAndToggleClash(hold, toggle).ShouldBeFalse();
    }
}
