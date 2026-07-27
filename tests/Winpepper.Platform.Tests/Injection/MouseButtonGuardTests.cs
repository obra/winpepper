using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class MouseButtonGuardTests
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkCancel = 0x03;  // NOT a mouse button
    private const int VkControl = 0x11; // modifier, not a mouse button

    [Fact]
    public void AnyDown_FalseWhenNothingHeld()
        => MouseButtonGuard.AnyDown(_ => false).ShouldBeFalse();

    [Theory]
    [InlineData(VkLButton)]
    [InlineData(VkRButton)]
    [InlineData(VkMButton)]
    public void AnyDown_TrueWhenAButtonIsHeld(int heldVk)
        => MouseButtonGuard.AnyDown(vk => vk == heldVk).ShouldBeTrue();

    [Fact]
    public void AnyDown_IgnoresNonMouseVks()
        => MouseButtonGuard.AnyDown(vk => vk is VkCancel or VkControl).ShouldBeFalse();

    [Fact]
    public void MouseVks_StayDisjointFromModifierVks()
        // ModifierVks drives WaitForRelease (1500 ms block) AND the KEYUP
        // neutralization prelude -- a mouse VK in that set would synthesize a
        // meaningless keyboard KEYUP for a mouse button. Keep the sets apart.
        => MouseButtonGuard.MouseButtonVks.Intersect(ModifierGuard.ModifierVks).ShouldBeEmpty();
}
