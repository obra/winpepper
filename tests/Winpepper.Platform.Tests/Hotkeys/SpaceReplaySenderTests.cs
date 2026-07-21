using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class SpaceReplaySenderTests
{
    [Fact]
    public void CompletePairSucceedsWithoutRepair()
    {
        var calls = new List<int>();
        var result = SpaceReplaySender.Send(inputs =>
        {
            calls.Add(inputs.Length);
            return 2;
        });

        result.Success.ShouldBeTrue();
        result.RepairAttempted.ShouldBeFalse();
        calls.ShouldBe(new[] { 2 });
    }

    [Fact]
    public void ZeroInputsIsReportedAsFailure()
    {
        var result = SpaceReplaySender.Send(_ => 0);

        result.Success.ShouldBeFalse();
        result.InitialInputsSent.ShouldBe(0u);
        result.RepairAttempted.ShouldBeFalse();
    }

    [Fact]
    public void PartialDownIsFollowedByStandaloneKeyUpRepair()
    {
        var calls = new List<int>();
        var result = SpaceReplaySender.Send(inputs =>
        {
            calls.Add(inputs.Length);
            if (calls.Count == 2)
            {
                inputs[0].Keyboard.Flags.ShouldBe(Winpepper.Platform.Injection.SendInputNative.KEYEVENTF_KEYUP);
                inputs[0].Keyboard.ExtraInfo.ShouldBe(HotkeyHook.SpaceReplayExtraInfo);
            }
            return calls.Count == 1 ? 1u : 1u;
        });

        result.Success.ShouldBeFalse();
        result.InitialInputsSent.ShouldBe(1u);
        result.RepairAttempted.ShouldBeTrue();
        result.RepairSucceeded.ShouldBeTrue();
        calls.ShouldBe(new[] { 2, 1 });
    }

    [Fact]
    public void FailedRepairIsReported()
    {
        var call = 0;
        var result = SpaceReplaySender.Send(_ => ++call == 1 ? 1u : 0u);

        result.Success.ShouldBeFalse();
        result.RepairAttempted.ShouldBeTrue();
        result.RepairSucceeded.ShouldBeFalse();
    }
}
