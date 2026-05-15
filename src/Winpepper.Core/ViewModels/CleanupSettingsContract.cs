namespace Winpepper.Core.ViewModels;

/// <summary>
/// Plan-3 settings record bound to the Cleanup tab. Profile values are the
/// string names of Plan 2's <c>Winpepper.Cleanup.CleanupProfile</c> enum
/// ("Ordinary", "Literal", "Custom"). Persistence into <c>CleanupOptions</c>
/// happens through the adapter in <see cref="CleanupSettingsViewModel"/>.
/// Marked PLAN2-TYPE for easy search.
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
        new(Enabled: true, WindowContextEnabled: false,
            Profile: "Ordinary", CustomPrompt: "",
            MaxNewTokens: 512, TimeoutMs: 15000);
}
