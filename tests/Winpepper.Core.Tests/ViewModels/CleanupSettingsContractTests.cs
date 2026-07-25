using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

/// <summary>
/// Pins the AppSettings &lt;-&gt; CleanupSettingsContract mapping that makes the
/// Cleanup tab durable: the persist callback writes ApplyTo(settings) and the
/// boot path reads FromSettings(settings). A field silently dropped from either
/// direction would be a dead setting again — the original defect.
/// </summary>
[Trait("Layer", "ViewModel")]
public class CleanupSettingsContractTests
{
    [Fact]
    public void FromSettings_Of_Default_Settings_Equals_Defaults()
    {
        CleanupSettingsContract.FromSettings(new AppSettings())
            .ShouldBe(CleanupSettingsContract.Defaults());
    }

    [Fact]
    public void FromSettings_Maps_Every_Field()
    {
        var settings = new AppSettings
        {
            CleanupEnabled = false,
            CleanupWindowContextEnabled = true,
            CleanupProfile = "Custom",
            CleanupCustomPrompt = "my prompt",
            CleanupMaxNewTokens = 1024,
            CleanupTimeoutMs = 30000,
        };
        var c = CleanupSettingsContract.FromSettings(settings);
        c.Enabled.ShouldBeFalse();
        c.WindowContextEnabled.ShouldBeTrue();
        c.Profile.ShouldBe("Custom");
        c.CustomPrompt.ShouldBe("my prompt");
        c.MaxNewTokens.ShouldBe(1024);
        c.TimeoutMs.ShouldBe(30000);
    }

    [Fact]
    public void ApplyTo_Then_FromSettings_RoundTrips()
    {
        var contract = new CleanupSettingsContract(
            Enabled: false, WindowContextEnabled: true,
            Profile: "Literal", CustomPrompt: "p",
            MaxNewTokens: 2048, TimeoutMs: 5000);

        var written = contract.ApplyTo(new AppSettings());
        CleanupSettingsContract.FromSettings(written).ShouldBe(contract);
    }

    [Fact]
    public void ApplyTo_Preserves_Unrelated_Settings()
    {
        var settings = new AppSettings { AsrProvider = "assemblyai", PlaySounds = false };
        var written = (CleanupSettingsContract.Defaults() with { Enabled = false })
            .ApplyTo(settings);
        written.AsrProvider.ShouldBe("assemblyai");
        written.PlaySounds.ShouldBeFalse();
        written.CleanupEnabled.ShouldBeFalse();
    }
}
