using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// End-user story: if the hotkey recorder control is torn down (window closed,
/// page unloaded) while it still has the global hook suspended for capture, the
/// hook must be un-suspended - otherwise every global hotkey is silently dead
/// until restart. The WinUI control is untestable on Linux, so the "always
/// release on teardown" guarantee lives in this pure-managed coordinator.
/// </summary>
public class RecorderSuspendCoordinatorTests
{
    [Fact]
    public void Teardown_WhileRecording_ReleasesSuspend()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);   // recorder armed -> suspend on
        coord.Teardown();           // control unloaded without Cancel/Commit

        states.ShouldBe(new[] { true, false });
        coord.IsSuspended.ShouldBeFalse();
    }

    [Fact]
    public void Teardown_WhenNotRecording_IsNoOp()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.Teardown();

        states.ShouldBeEmpty();
        coord.IsSuspended.ShouldBeFalse();
    }

    [Fact]
    public void Teardown_AfterNormalStop_DoesNotDoubleRelease()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);
        coord.SetRecording(false);  // committed / cancelled normally
        coord.Teardown();           // later unload

        states.ShouldBe(new[] { true, false });
    }

    [Fact]
    public void SetRecording_IsIdempotentPerState()
    {
        var states = new List<bool>();
        var coord = new RecorderSuspendCoordinator(states.Add);

        coord.SetRecording(true);
        coord.SetRecording(true);
        coord.SetRecording(false);
        coord.SetRecording(false);

        states.ShouldBe(new[] { true, false });
    }
}
