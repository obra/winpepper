using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests;

public sealed class AppSettingsDefaultsTests
{
    [Fact]
    public void Defaults_UseLocalProvider()
    {
        var s = new AppSettings();
        s.AsrProvider.ShouldBe("local");
    }

    [Fact]
    public void Defaults_UseFastAssemblyAiModel()
    {
        var s = new AppSettings();
        s.AssemblyAiModel.ShouldBe("universal-2");
    }

    [Fact]
    public void AssemblyAi_Retention_Deadline_Keyterms_Defaults()
    {
        var s = new AppSettings();
        s.AssemblyAiDeleteAfterTranscribe.ShouldBeTrue();     // privacy default: delete
        s.AssemblyAiCloudDeadlineSeconds.ShouldBe(10);        // single owned budget
        s.AssemblyAiKeytermsEnabled.ShouldBeFalse();          // opt-in, may cost extra
    }
}
