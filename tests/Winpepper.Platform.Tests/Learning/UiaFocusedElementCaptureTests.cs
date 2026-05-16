using Shouldly;
using Winpepper.Platform.Learning;
using Xunit;

namespace Winpepper.Platform.Tests.Learning;

public class UiaFocusedElementCaptureTests
{
    [Fact]
    public void RuntimeIdToString_Joins_Ints_With_Dots_For_Stable_Key()
    {
        var key = UiaFocusedElementCapture.RuntimeIdToString(new[] { 42, 7, 1, 5 });
        key.ShouldBe("42.7.1.5");
    }

    [Fact]
    public void RuntimeIdToString_Empty_Array_Returns_Empty_String()
    {
        UiaFocusedElementCapture.RuntimeIdToString(Array.Empty<int>()).ShouldBe("");
    }

    [Fact]
    public void RuntimeIdToString_Null_Array_Returns_Empty_String()
    {
        UiaFocusedElementCapture.RuntimeIdToString(null).ShouldBe("");
    }
}
