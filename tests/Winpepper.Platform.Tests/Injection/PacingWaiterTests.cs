using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class PacingWaiterTests
{
    [Fact]
    public void Wait_NonPositive_ReturnsImmediately()
    {
        var sw = Stopwatch.StartNew();
        PacingWaiter.Wait(0);
        PacingWaiter.Wait(-5);
        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(50);
    }

    [Fact]
    public void Wait_ActuallyWaits_OnEveryPlatform()
    {
        // On Windows this exercises the high-res timer path; elsewhere the
        // Thread.Sleep fallback. Either way Wait(25) must actually block.
        var sw = Stopwatch.StartNew();
        PacingWaiter.Wait(25);
        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(15);
    }
}
