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
    public void Defaults_StreamingEnabled_IsTrue()
    {
        // ON by default (2026-07-25): safe in every configuration. AssemblyAI
        // streams with local batch fallback; local with the Nemotron streaming
        // model streams via transcribe.cpp; local without it uses the batch
        // adapter (identical to the old default). The chunked-TDT streaming
        // attempt (blank-collapse defect) is no longer wired for local.
        var s = new AppSettings();
        s.StreamingEnabled.ShouldBeTrue();
    }

    [Fact]
    public void Defaults_InjectionChannels_IsFullLadderOrder()
    {
        new AppSettings().InjectionChannels.ShouldBe(
            new[] { "emReplaceSel", "wmCharSmto", "vkPacket" });
    }
}
