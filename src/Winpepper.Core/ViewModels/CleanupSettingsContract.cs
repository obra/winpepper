using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Plan-3 settings record bound to the Cleanup tab. Profile values are the
/// string names of Plan 2's <c>Winpepper.Cleanup.CleanupProfile</c> enum
/// ("Ordinary", "Literal", "Custom"). Persisted into <see cref="AppSettings"/>
/// (Cleanup* properties) via <see cref="ApplyTo"/>; PipelineHost reads those
/// settings live per dictation. Marked PLAN2-TYPE for easy search.
/// </summary>
public sealed record CleanupSettingsContract(
    bool Enabled,
    bool WindowContextEnabled,
    string Profile,
    string CustomPrompt,
    int MaxNewTokens,
    int TimeoutMs)
{
    public static CleanupSettingsContract Defaults() =>
        new(Enabled: false, WindowContextEnabled: false,
            Profile: "Ordinary", CustomPrompt: "",
            MaxNewTokens: 512, TimeoutMs: 15000);

    /// <summary>Load the contract from the persisted settings (Cleanup tab boot value).</summary>
    public static CleanupSettingsContract FromSettings(AppSettings settings) =>
        new(Enabled: settings.CleanupEnabled,
            WindowContextEnabled: settings.CleanupWindowContextEnabled,
            Profile: settings.CleanupProfile,
            CustomPrompt: settings.CleanupCustomPrompt,
            MaxNewTokens: settings.CleanupMaxNewTokens,
            TimeoutMs: settings.CleanupTimeoutMs);

    /// <summary>Write the contract into the persisted settings (persist callback).</summary>
    public AppSettings ApplyTo(AppSettings settings) => settings with
    {
        CleanupEnabled = Enabled,
        CleanupWindowContextEnabled = WindowContextEnabled,
        CleanupProfile = Profile,
        CleanupCustomPrompt = CustomPrompt,
        CleanupMaxNewTokens = MaxNewTokens,
        CleanupTimeoutMs = TimeoutMs,
    };
}
