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
    public void SetPending_HoldsTextAndTarget()
    {
        var s = new PendingPasteState();
        s.SetPending("hello world", T(5, "1.2"));
        s.HasPending.ShouldBeTrue();
        s.PendingText.ShouldBe("hello world");
        s.Target.ShouldBe(T(5, "1.2"));
    }

    [Fact]
    public void SetPending_ReplacesExisting()
    {
        var s = new PendingPasteState();
        s.SetPending("first", T(1, "a"));
        s.SetPending("second", T(2, "b"));
        s.PendingText.ShouldBe("second");
        s.Target.ShouldBe(T(2, "b"));
    }

    [Fact]
    public void Discard_ClearsSlot()
    {
        var s = new PendingPasteState();
        s.SetPending("gone", T(1, "a"));
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
        s.SetPending("place me", T(1, "a"));
        var consumed = s.OnPasteAttempted(injected: true);
        consumed.ShouldBeTrue();
        s.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void OnPasteAttempted_Failure_KeepsSlotForRetry()
    {
        var s = new PendingPasteState();
        s.SetPending("keep me", T(1, "a"));
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
