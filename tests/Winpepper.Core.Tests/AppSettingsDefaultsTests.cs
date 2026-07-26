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
    public void Defaults_UseLatestAssemblyAiModel()
    {
        var s = new AppSettings();
        s.AssemblyAiModel.ShouldBe("universal-3-5-pro");
    }

    [Fact]
    public void AssemblyAi_Retention_Deadline_Keyterms_Defaults()
    {
        var s = new AppSettings();
        s.AssemblyAiDeleteAfterTranscribe.ShouldBeTrue();     // privacy default: delete
        s.AssemblyAiCloudDeadlineSeconds.ShouldBe(10);        // single owned budget
        s.AssemblyAiKeytermsEnabled.ShouldBeFalse();          // opt-in, may cost extra
    }

    [Fact]
    public void Defaults_StreamingEnabled_IsFalse()
    {
        // OFF by default: real-model chunked streaming decodes to blanks after
        // the first ~2 s chunk (2026-07-25 validation; ParakeetStreamingSession
        // class doc), so streaming is opt-in until that defect is fixed.
        var s = new AppSettings();
        s.StreamingEnabled.ShouldBeFalse();
    }
}
