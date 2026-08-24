using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Pins the per-dictation AppSettings -> CleanupOptions mapping PipelineHost
/// uses. Every Cleanup-tab setting must arrive in the options the runner sees,
/// and out-of-range values from a hand-edited settings file must be clamped to
/// the same ranges the Cleanup tab enforces.
/// </summary>
public class CleanupOptionsFactoryTests
{
    [Fact]
    public void FromSettings_Maps_Every_Cleanup_Field()
    {
        var settings = new AppSettings
        {
            CleanupEnabled = false,
            CleanupWindowContextEnabled = true,
            CleanupProfile = "Custom",
            CleanupCustomPrompt = "my base prompt",
            CleanupMaxNewTokens = 1024,
            CleanupTimeoutMs = 30000,
        };

        var opts = CleanupOptionsFactory.FromSettings(settings);

        opts.Enabled.ShouldBeFalse();
        opts.WindowContextEnabled.ShouldBeTrue();
        opts.Profile.ShouldBe(CleanupProfile.Custom);
        opts.CustomBasePrompt.ShouldBe("my base prompt");
        opts.MaxNewTokensCap.ShouldBe(1024);
        opts.Timeout.ShouldBe(TimeSpan.FromMilliseconds(30000));
    }

    [Fact]
    public void FromSettings_Defaults_Match_AppSettings_Defaults()
    {
        var opts = CleanupOptionsFactory.FromSettings(new AppSettings());

        opts.Enabled.ShouldBeFalse(); // 2026-08-24: cleanup LLM is opt-in (minimal-footprint default)
        opts.WindowContextEnabled.ShouldBeFalse();
        opts.Profile.ShouldBe(CleanupProfile.Ordinary);
        opts.MaxNewTokensCap.ShouldBe(512);
        opts.Timeout.ShouldBe(TimeSpan.FromMilliseconds(15000));
    }

    [Fact]
    public void FromSettings_Clamps_HandEdited_OutOfRange_Values()
    {
        var low = CleanupOptionsFactory.FromSettings(new AppSettings
        {
            CleanupMaxNewTokens = 1,
            CleanupTimeoutMs = 1,
        });
        low.MaxNewTokensCap.ShouldBe(64);
        low.Timeout.ShouldBe(TimeSpan.FromMilliseconds(2000));

        var high = CleanupOptionsFactory.FromSettings(new AppSettings
        {
            CleanupMaxNewTokens = 1_000_000,
            CleanupTimeoutMs = 1_000_000,
        });
        high.MaxNewTokensCap.ShouldBe(4096);
        high.Timeout.ShouldBe(TimeSpan.FromMilliseconds(60000));
    }

    [Theory]
    [InlineData("Ordinary", CleanupProfile.Ordinary)]
    [InlineData("Literal", CleanupProfile.Literal)]
    [InlineData("Custom", CleanupProfile.Custom)]
    [InlineData("garbage", CleanupProfile.Ordinary)] // unknown -> safe default
    [InlineData("", CleanupProfile.Ordinary)]
    [InlineData(null, CleanupProfile.Ordinary)]
    public void ParseProfile_Maps_Names_And_Falls_Back_To_Ordinary(string? name, CleanupProfile expected)
    {
        CleanupOptionsFactory.ParseProfile(name).ShouldBe(expected);
    }
}
