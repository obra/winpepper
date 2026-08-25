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

    [Fact]
    public void Defaults_Cleanup_IsOptIn()
    {
        // 2026-08-24 minimal-footprint default: a fresh install downloads and
        // loads ONLY the streaming speech model (~756 MB). The backup ASR and
        // the cleanup LLM are opt-in (onboarding checkboxes, Cleanup tab
        // toggle), so cleanup boots OFF when no choice was ever persisted.
        // Existing settings files carry cleanupEnabled=true and are unaffected.
        var s = new AppSettings();
        s.CleanupEnabled.ShouldBeFalse();
        Winpepper.Core.ViewModels.CleanupSettingsContract.Defaults().Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Defaults_BackupAsr_IsNone()
    {
        // 2026-08-25: "" means None — no backup model is selected, downloaded,
        // or loaded; the streaming model runs primary-only (the Models tab's
        // backup combo shows its "None" entry). ModelRegistry.ResolveOrDefault
        // still names parakeet as the REPAIR default for unknown non-empty
        // persisted values; and settings files from before this change carry
        // the old parakeet default explicitly, so they keep their backup.
        new AppSettings().AsrModelName.ShouldBe("");
    }
}
