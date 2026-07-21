using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: a hand-edited settings file (or a future UI path) must not be
/// able to bind a bare common key (Esc, Tab, Enter, Space, a letter, a digit, an
/// arrow) as a hold/toggle trigger, because the hook swallows the trigger and
/// that key would then be dead system-wide - the exact "Esc broken everywhere"
/// failure. Bare F-keys stay allowed (conventional global hotkeys); modifier+key
/// and modifier-only chords stay allowed; the Cancel key can never be a trigger.
/// </summary>
public class HotkeyChordValidationTests
{
    private static readonly HotkeyChord Cancel = HotkeyChord.Parse("Esc");

    [Theory]
    [InlineData("Esc")]
    [InlineData("Tab")]
    [InlineData("Enter")]
    [InlineData("Back")]
    [InlineData("Space")]
    [InlineData("Delete")]
    [InlineData("Left")]
    [InlineData("A")]
    [InlineData("5")]
    public void ValidateTriggerBinding_BareCommonKey_IsRejected(string chord)
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse(chord), Cancel).ShouldNotBeNull();

    [Theory]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("LeftAlt+F12")]
    [InlineData("LeftCtrl+A")]
    [InlineData("RightCtrl+RightShift")] // modifier-only: trigger is a modifier
    [InlineData("F1")]                   // bare F-keys are allowed
    [InlineData("F12")]
    [InlineData("F24")]
    [InlineData("Application")]
    [InlineData("Copilot")]
    [InlineData("Alt+Win")]
    public void ValidateTriggerBinding_SafeBinding_IsAccepted(string chord)
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse(chord), Cancel).ShouldBeNull();

    [Fact]
    public void ValidateTriggerBinding_TriggerEqualsCancelKey_IsRejected()
        => HotkeyChord.ValidateTriggerBinding(HotkeyChord.Parse("Ctrl+Esc"), Cancel).ShouldNotBeNull();

    [Fact]
    public void ParseTriggerOrDefault_UnsafeBareEsc_FallsBackToDefault_AndWarns()
    {
        string? warned = null;
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "Esc", "Ctrl+Shift+Space", Cancel, m => warned = m);

        chord.ShouldBe(HotkeyChord.Parse("Ctrl+Shift+Space"));
        warned.ShouldNotBeNull();
    }

    [Fact]
    public void ParseTriggerOrDefault_SafeBinding_IsKept()
    {
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "LeftAlt+F12", "Ctrl+Shift+Space", Cancel);

        chord.ShouldBe(HotkeyChord.Parse("LeftAlt+F12"));
    }

    [Fact]
    public void ParseTriggerOrDefault_UnparseableValue_FallsBackToDefault()
    {
        var chord = HotkeyChord.ParseTriggerOrDefault(
            "Ctrl+NotAKey", "RightCtrl+RightShift", Cancel);

        chord.ShouldBe(HotkeyChord.Parse("RightCtrl+RightShift"));
    }

    [Fact]
    public void ParseTriggerOrDefault_AllowsSpaceOnlyForExplicitLongPressHoldPolicy()
    {
        HotkeyChord.ParseTriggerOrDefault(
            "Space", "RightCtrl+RightShift", Cancel,
            allowLongPressSpace: true).ShouldBe(HotkeyChord.Parse("Space"));

        HotkeyChord.ParseTriggerOrDefault(
            "Space", "Ctrl+Shift+Space", Cancel,
            allowLongPressSpace: false).ShouldBe(HotkeyChord.Parse("Ctrl+Shift+Space"));
    }
}
