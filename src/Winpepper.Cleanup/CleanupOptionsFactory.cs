using Winpepper.Core.Settings;

namespace Winpepper.Cleanup;

/// <summary>
/// Builds per-dictation <see cref="CleanupOptions"/> from the persisted
/// <see cref="AppSettings"/>. PipelineHost calls this on every dictation so a
/// Cleanup-tab change (including the Enabled toggle) takes effect immediately —
/// no boot-frozen options snapshot. Clamp ranges mirror
/// <c>CleanupSettingsViewModel</c> so a hand-edited settings file cannot smuggle
/// out-of-range values into the runner.
/// </summary>
public static class CleanupOptionsFactory
{
    public static CleanupOptions FromSettings(AppSettings settings) => new()
    {
        Enabled = settings.CleanupEnabled,
        Profile = ParseProfile(settings.CleanupProfile),
        CustomBasePrompt = settings.CleanupCustomPrompt,
        Timeout = TimeSpan.FromMilliseconds(Math.Clamp(settings.CleanupTimeoutMs, 2000, 60000)),
        WindowContextEnabled = settings.CleanupWindowContextEnabled,
        MaxNewTokensCap = Math.Clamp(settings.CleanupMaxNewTokens, 64, 4096),
    };

    /// <summary>Unknown/null profile strings fall back to Ordinary.</summary>
    public static CleanupProfile ParseProfile(string? s) => s switch
    {
        "Literal" => CleanupProfile.Literal,
        "Custom"  => CleanupProfile.Custom,
        _         => CleanupProfile.Ordinary,
    };
}
