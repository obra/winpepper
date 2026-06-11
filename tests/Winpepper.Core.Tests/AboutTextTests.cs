using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class AboutTextTests
{
    [Fact]
    public void Title_StartsWithProductName()
    {
        AboutText.Title.ShouldStartWith("Winpepper");
    }

    [Fact]
    public void Body_ContainsVersionAndUnsignedMarker()
    {
        var body = AboutText.Body();
        body.ShouldContain("Version");
        body.ShouldMatch(@"\d+\.\d+\.\d+");  // tracks version.json instead of pinning a literal
        body.ShouldContain("(unsigned build)");
    }
}
