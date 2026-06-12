using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class BuildSignatureTests
{
    [Fact]
    public void Describe_IncludesAssemblyInformationalVersion()
    {
        var s = BuildSignature.Describe();
        s.ShouldMatch(@"\d+\.\d+\.\d+");  // tracks version.json instead of pinning a literal
    }

    [Fact]
    public void Describe_FlagsUnsignedBuildWhenSignedConstantAbsent()
    {
        // Default dev build is unsigned; constant WINPEPPER_SIGNED is not defined.
        var s = BuildSignature.Describe();
        s.ShouldContain("(unsigned build)");
    }
}
