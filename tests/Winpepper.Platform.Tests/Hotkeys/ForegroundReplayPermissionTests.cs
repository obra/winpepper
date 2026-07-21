using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class ForegroundReplayPermissionTests
{
    [Theory]
    [InlineData(0x2000u, 0x2000u, true)]
    [InlineData(0x3000u, 0x2000u, true)]
    [InlineData(0x2000u, 0x3000u, false)]
    public void IntegrityComparisonAllowsOnlyEqualOrLowerTargets(
        uint current, uint target, bool expected)
    {
        ForegroundReplayPermission.IsPermitted(current, target).ShouldBe(expected);
    }

    [Fact]
    public void UnknownIntegrityFailsClosed()
    {
        ForegroundReplayPermission.IsPermitted(null, 0x2000u).ShouldBeFalse();
        ForegroundReplayPermission.IsPermitted(0x2000u, null).ShouldBeFalse();
    }
}
