using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class SanityTests
{
    [Fact]
    public void AssemblyLoads()
    {
        typeof(Winpepper.Cleanup.Placeholder).FullName.ShouldBe("Winpepper.Cleanup.Placeholder");
    }
}
