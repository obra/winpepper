using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class InjectionTargetTests
{
    private static InjectionTarget Make(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void Empty_IsNotValid()
    {
        InjectionTarget.Empty.IsValid.ShouldBeFalse();
        InjectionTarget.Empty.WindowHandle.ShouldBe(0L);
        InjectionTarget.Empty.ElementId.ShouldBe("");
    }

    [Fact]
    public void IsValid_TrueWhenElementIdPresent()
    {
        Make(42, "7.3.11").IsValid.ShouldBeTrue();
        Make(42, "").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Matches_TrueForSameWindowAndElement()
    {
        Make(42, "7.3.11").Matches(Make(42, "7.3.11")).ShouldBeTrue();
    }

    [Fact]
    public void Matches_FalseWhenElementDiffers()
    {
        Make(42, "7.3.11").Matches(Make(42, "9.9.9")).ShouldBeFalse();
    }

    [Fact]
    public void Matches_FalseWhenWindowDiffers()
    {
        Make(42, "7.3.11").Matches(Make(99, "7.3.11")).ShouldBeFalse();
    }
}
