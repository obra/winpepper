using Shouldly;
using Xunit;

namespace Winpepper.Core.Tests;

public class SignScriptTests
{
    private static string ScriptPath()
    {
        var here = AppContext.BaseDirectory;
        // Walk up to repo root from bin/.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "winpepper.sln")))
            dir = dir.Parent;
        dir.ShouldNotBeNull();
        return Path.Combine(dir!.FullName, "packaging", "sign.ps1");
    }

    [Fact]
    public void ScriptExists()
    {
        File.Exists(ScriptPath()).ShouldBeTrue();
    }

    [Fact]
    public void Script_HasThumbprintAndPfxParameters()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("param(");
        txt.ShouldContain("$Thumbprint");
        txt.ShouldContain("$PfxPath");
        txt.ShouldContain("$PfxPassword");
        txt.ShouldContain("$InputFiles");
    }

    [Fact]
    public void Script_DisabledMessage()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("WINPEPPER_SIGNING_DISABLED");
    }

    [Fact]
    public void Script_InvokesSigntool()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("signtool");
        txt.ShouldContain("/sha1");
        txt.ShouldContain("/f");
        // EV certs require the SHA256 file digest and an RFC 3161 timestamp server.
        txt.ShouldContain("/fd SHA256");
        txt.ShouldContain("/tr ");
        txt.ShouldContain("/td SHA256");
    }
}
