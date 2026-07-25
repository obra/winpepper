using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class AppSettingsCleanupModelTests
{
    [Fact]
    public void Defaults_Include_CleanupModelName()
    {
        var s = new AppSettings();
        s.CleanupModelName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void With_RoundTrips()
    {
        var s = new AppSettings() with { CleanupModelName = "custom-model" };
        s.CleanupModelName.ShouldBe("custom-model");
    }

    /// <summary>Defaults mirror CleanupSettingsContract.Defaults() so a fresh
    /// install behaves exactly as the Cleanup tab presents it.</summary>
    [Fact]
    public void Defaults_Include_Cleanup_Llm_Settings()
    {
        var s = new AppSettings();
        s.CleanupEnabled.ShouldBeTrue();
        s.CleanupWindowContextEnabled.ShouldBeFalse();
        s.CleanupProfile.ShouldBe("Ordinary");
        s.CleanupCustomPrompt.ShouldBe("");
        s.CleanupMaxNewTokens.ShouldBe(512);
        s.CleanupTimeoutMs.ShouldBe(15000);
    }
}
