using System.Reflection;
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class VersionStampTests
{
    [Fact]
    public void AssemblyInformationalVersion_IsNotEmpty()
    {
        var asm = typeof(HelloWinpepper).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        info.ShouldNotBeNull();
        info!.InformationalVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AssemblyVersion_MatchesMajorMinorPatchFromVersionJson()
    {
        // version.json declares 0.6.0-alpha; Nerdbank.GitVersioning sets AssemblyVersion
        // to 0.6.0.{git-height}. We only assert the major/minor/build prefix.
        var v = typeof(HelloWinpepper).Assembly.GetName().Version!;
        v.Major.ShouldBe(0);
        v.Minor.ShouldBe(6);
        v.Build.ShouldBe(0);
    }
}
