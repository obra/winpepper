using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class PendingPasteStateTests
{
    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void Fresh_HasNoPending()
    {
        var s = new PendingPasteState();
        s.HasPending.ShouldBeFalse();
        s.PendingText.ShouldBe("");
        s.Target.ShouldBe(InjectionTarget.Empty);
    }

    [Fact]
    public void HoldOrAppend_Fresh_HoldsTextTargetAndReason()
    {
        var state = new PendingPasteState();
        var target = new InjectionTarget { WindowHandle = 42, ElementId = "el" };

        state.HoldOrAppend("hello world", target, PendingPasteReason.ElevatedTarget);

        state.HasPending.ShouldBeTrue();
        state.PendingText.ShouldBe("hello world");
        state.Target.ShouldBe(target);
        state.Reason.ShouldBe(PendingPasteReason.ElevatedTarget);
    }

    [Fact]
    public void HoldOrAppend_Occupied_Appends_WithSpaceSeparator()
    {
        // DELIBERATE PIN REVISION (council 2026-07-28, "preserve/append or
        // fail loud -- never silently drop"; supersedes the 2026-07-21
        // pending-paste plan's replace semantics): an occupied slot APPENDS,
        // oldest first, so one pill click pastes everything. Separator is a
        // SPACE, not a newline -- injected text is typed as keystrokes and
        // Enter submits in many chat inputs.
        var state = new PendingPasteState();
        state.HoldOrAppend("first thought.", T(1, "a"),
            PendingPasteReason.ElevatedTarget);

        var newTarget = T(2, "b");
        state.HoldOrAppend("second thought.", newTarget, PendingPasteReason.Interrupted);

        state.PendingText.ShouldBe("first thought. second thought.");
        state.Target.ShouldBe(newTarget);                       // latest context wins
        state.Reason.ShouldBe(PendingPasteReason.Interrupted);  // latest reason drives the copy
        state.HasPending.ShouldBeTrue();
    }

    [Fact]
    public void HoldOrAppend_Occupied_EmptyIncoming_KeepsExistingText()
    {
        var state = new PendingPasteState();
        state.HoldOrAppend("kept", T(1, "a"),
            PendingPasteReason.Interrupted);

        state.HoldOrAppend("", T(2, "b"),
            PendingPasteReason.Interrupted);

        state.PendingText.ShouldBe("kept"); // never degrade held text
        state.HasPending.ShouldBeTrue();
    }

    [Fact]
    public void Discard_ResetsReasonToDefault()
    {
        var state = new PendingPasteState();
        state.HoldOrAppend("t", T(1, "a"),
            PendingPasteReason.ElevatedTarget);

        state.Discard();

        state.Reason.ShouldBe(PendingPasteReason.Interrupted);
    }

    [Fact]
    public void Discard_ClearsSlot()
    {
        var s = new PendingPasteState();
        s.HoldOrAppend("gone", T(1, "a"), PendingPasteReason.Interrupted);
        s.Discard();
        s.HasPending.ShouldBeFalse();
        s.PendingText.ShouldBe("");
    }

    [Fact]
    public void Discard_IsIdempotent()
    {
        var s = new PendingPasteState();
        Should.NotThrow(() => s.Discard());
        s.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void OnPasteAttempted_Success_ConsumesSlot()
    {
        var s = new PendingPasteState();
        s.HoldOrAppend("place me", T(1, "a"), PendingPasteReason.Interrupted);
        var consumed = s.OnPasteAttempted(injected: true);
        consumed.ShouldBeTrue();
        s.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void OnPasteAttempted_Failure_KeepsSlotForRetry()
    {
        var s = new PendingPasteState();
        s.HoldOrAppend("keep me", T(1, "a"), PendingPasteReason.Interrupted);
        var consumed = s.OnPasteAttempted(injected: false);
        consumed.ShouldBeFalse();
        s.HasPending.ShouldBeTrue();
        s.PendingText.ShouldBe("keep me");
    }

    [Fact]
    public void OnPasteAttempted_NoPending_ReturnsFalse()
    {
        var s = new PendingPasteState();
        s.OnPasteAttempted(injected: true).ShouldBeFalse();
    }
}
