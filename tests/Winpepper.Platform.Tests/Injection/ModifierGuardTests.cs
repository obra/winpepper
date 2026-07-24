using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class ModifierGuardTests
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;

    [Fact]
    public void AnyDown_FalseWhenNoModifierHeld()
        => ModifierGuard.AnyDown(_ => false).ShouldBeFalse();

    [Fact]
    public void AnyDown_TrueWhenAnyModifierHeld()
        => ModifierGuard.AnyDown(vk => vk == VkControl).ShouldBeTrue();

    [Fact]
    public void HeldModifiers_ReturnsExactlyTheHeldKeys()
        => ModifierGuard.HeldModifiers(vk => vk is VkControl or VkShift)
            .ShouldBe(new[] { VkShift, VkControl });

    [Fact]
    public void WaitForRelease_ImmediateWhenNothingHeld()
    {
        var slept = 0;
        ModifierGuard.WaitForRelease(() => false, 1500, 15, _ => slept++).ShouldBeTrue();
        slept.ShouldBe(0); // no polling needed
    }

    [Fact]
    public void WaitForRelease_ReturnsTrueOnceKeysAreReleased()
    {
        var polls = 0;
        // Held for the first 3 polls, released afterwards.
        ModifierGuard.WaitForRelease(() => polls < 3, 1500, 15, _ => polls++).ShouldBeTrue();
        polls.ShouldBeLessThan(10); // returned soon after release, not at timeout
    }

    [Fact]
    public void WaitForRelease_FalseWhenHeldPastTimeout()
    {
        var sleptMs = 0;
        ModifierGuard.WaitForRelease(() => true, 150, 15, ms => sleptMs += ms).ShouldBeFalse();
        sleptMs.ShouldBe(150); // waited the full budget, no longer
    }

    [Fact]
    public void WaitForRelease_RejectsNonPositivePoll()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => ModifierGuard.WaitForRelease(() => true, 100, 0, _ => { }));

    [Fact]
    public void BuildKeyUpInputs_OneKeyUpPerHeldModifier()
    {
        var inputs = ModifierGuard.BuildKeyUpInputs(new[] { VkShift, VkControl });
        inputs.Length.ShouldBe(2);
        foreach (var i in inputs)
        {
            i.Type.ShouldBe(SendInputNative.INPUT_KEYBOARD);
            (i.Keyboard.Flags & SendInputNative.KEYEVENTF_KEYUP).ShouldBe(SendInputNative.KEYEVENTF_KEYUP);
            (i.Keyboard.Flags & SendInputNative.KEYEVENTF_UNICODE).ShouldBe(0u); // VK-based, not unicode
        }
        inputs[0].Keyboard.Vk.ShouldBe((ushort)VkShift);
        inputs[1].Keyboard.Vk.ShouldBe((ushort)VkControl);
    }
}
