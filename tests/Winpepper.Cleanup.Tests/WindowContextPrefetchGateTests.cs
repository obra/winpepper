using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class WindowContextPrefetchGateTests
{
    [Fact]
    public void Prefetches_When_Enabled_And_Format_Carries_System_Prompt()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, "chatml").ShouldBeTrue();

    [Fact]
    public void Skips_When_Active_Model_Is_RawIo_Even_With_Settings_On()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, CleanupPromptFormatter.RawIo)
            .ShouldBeFalse();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Skips_When_Either_Setting_Is_Off(bool cleanupEnabled, bool ctxEnabled)
        => WindowContextPrefetchGate.ShouldPrefetch(cleanupEnabled, ctxEnabled, "chatml")
            .ShouldBeFalse();

    [Fact]
    public void Unknown_Format_Behaves_As_Today_And_Prefetches()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, null).ShouldBeTrue();
}
