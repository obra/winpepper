using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class SelftestProbeTests
{
    [Fact]
    public void Run_ReturnsZero_AndEmitsExpectedToken()
    {
        var sb = new System.Text.StringBuilder();
        var code = SelftestProbe.Run(line => sb.AppendLine(line));
        code.ShouldBe(0);
        sb.ToString().ShouldContain("WINPEPPER_SELFTEST_OK");
        sb.ToString().ShouldContain(BuildSignature.Describe());
    }
}
