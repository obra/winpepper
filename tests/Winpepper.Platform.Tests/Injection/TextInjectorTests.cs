using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorTests
{
    [Theory]
    [InlineData("a", new ushort[] { 0x0061 })]
    [InlineData("ab", new ushort[] { 0x0061, 0x0062 })]
    [InlineData("é", new ushort[] { 0x00E9 })]
    [InlineData("中", new ushort[] { 0x4E2D })]
    // U+1F600 (grinning face) = surrogate pair D83D DE00
    [InlineData("😀", new ushort[] { 0xD83D, 0xDE00 })]
    [InlineData("ab中😀",
        new ushort[] { 0x0061, 0x0062, 0x4E2D, 0xD83D, 0xDE00 })]
    public void ToCodeUnits_HandlesAscii_NonAscii_AndSurrogatePairs(string text, ushort[] expected)
    {
        TextInjector.ToCodeUnits(text).ShouldBe(expected);
    }

    [Fact]
    public void BuildKeyDownUpInputs_ProducesTwoInputsPerCodeUnit_WithUnicodeFlag()
    {
        var inputs = TextInjector.BuildKeyDownUpInputs(new ushort[] { 0x0041 });
        inputs.Length.ShouldBe(2);
        inputs[0].Keyboard.Scan.ShouldBe((ushort)0x0041);
        (inputs[0].Keyboard.Flags & SendInputNative.KEYEVENTF_UNICODE).ShouldBe(SendInputNative.KEYEVENTF_UNICODE);
        (inputs[1].Keyboard.Flags & SendInputNative.KEYEVENTF_KEYUP).ShouldBe(SendInputNative.KEYEVENTF_KEYUP);
    }
}
