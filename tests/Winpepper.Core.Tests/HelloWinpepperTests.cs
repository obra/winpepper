using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class HelloWinpepperTests
{
    [Fact]
    public void Greeting_HasExpectedValue()
    {
        HelloWinpepper.Greeting.ShouldBe("Winpepper online.");
    }
}
