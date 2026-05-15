using Shouldly;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class SanityTests
{
    [Fact]
    public void AssemblyLoads()
    {
        typeof(Winpepper.Corrections.Placeholder).FullName.ShouldBe("Winpepper.Corrections.Placeholder");
    }
}
