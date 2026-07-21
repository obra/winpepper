using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class PendingPasteDeciderTests
{
    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void SameTarget_InjectsNow()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(1, "a.b"))
            .ShouldBe(InjectionDecision.InjectNow);
    }

    [Fact]
    public void DifferentTarget_HoldsPending()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(2, "c.d"))
            .ShouldBe(InjectionDecision.HoldPending);
    }

    [Fact]
    public void SameWindowDifferentElement_HoldsPending()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(1, "z.z"))
            .ShouldBe(InjectionDecision.HoldPending);
    }

    [Fact]
    public void UnknownStartTarget_InjectsNow()
    {
        // Could not capture identity at start -> preserve today's behavior.
        PendingPasteDecider.Decide(InjectionTarget.Empty, T(2, "c.d"))
            .ShouldBe(InjectionDecision.InjectNow);
    }

    [Fact]
    public void UnknownInjectTarget_InjectsNow()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), InjectionTarget.Empty)
            .ShouldBe(InjectionDecision.InjectNow);
    }
}
