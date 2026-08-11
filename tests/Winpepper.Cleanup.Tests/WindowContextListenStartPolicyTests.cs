using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class WindowContextListenStartPolicyTests
{
    [Fact]
    public void Starts_When_All_Enabled_And_Hwnd_NonZero()
        => WindowContextListenStartPolicy.ShouldStart(true, true, "chatml", hwndAtStartNonZero: true)
            .ShouldBeTrue();

    [Fact]
    public void Skips_When_Cleanup_Disabled_Even_With_WindowContext_And_Hwnd()
        => WindowContextListenStartPolicy.ShouldStart(false, true, "chatml", hwndAtStartNonZero: true)
            .ShouldBeFalse();

    [Fact]
    public void Skips_When_WindowContext_Disabled()
        => WindowContextListenStartPolicy.ShouldStart(true, false, "chatml", hwndAtStartNonZero: true)
            .ShouldBeFalse();

    [Fact]
    public void Skips_When_Active_Model_Is_RawIo_Even_With_Settings_On_And_Hwnd()
        => WindowContextListenStartPolicy.ShouldStart(
                true, true, CleanupPromptFormatter.RawIo, hwndAtStartNonZero: true)
            .ShouldBeFalse();

    [Fact]
    public void Starts_When_ActivePromptFormat_Null_And_Rest_Enabled()
        => WindowContextListenStartPolicy.ShouldStart(true, true, null, hwndAtStartNonZero: true)
            .ShouldBeTrue();

    [Fact]
    public void Skips_When_HwndAtStart_Is_Zero_Rest_Enabled()
        => WindowContextListenStartPolicy.ShouldStart(true, true, "chatml", hwndAtStartNonZero: false)
            .ShouldBeFalse();
}