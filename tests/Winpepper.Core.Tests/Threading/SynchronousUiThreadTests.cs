using Shouldly;
using Winpepper.Core.Threading;
using Xunit;

namespace Winpepper.Core.Tests.Threading;

public class SynchronousUiThreadTests
{
    [Fact]
    public void Post_Runs_Callback_Inline()
    {
        var ui = new SynchronousUiThread();
        var ran = 0;
        ui.Post(() => ran++);
        ran.ShouldBe(1);
    }

    [Fact]
    public void HasThreadAccess_Is_True_For_Synchronous()
    {
        var ui = new SynchronousUiThread();
        ui.HasThreadAccess.ShouldBeTrue();
    }
}
