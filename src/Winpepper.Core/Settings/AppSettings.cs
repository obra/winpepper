namespace Winpepper.Core.Settings;

/// <summary>
/// Persisted user settings. Schema-versioned for forward compatibility.
/// Defaults are returned when the file is missing or corrupt.
/// </summary>
public record AppSettings
{
    public int Schema { get; init; } = 1;

    // Audio
    public string MicDeviceId { get; init; } = "";

    // ASR
    public string AsrModelName { get; init; } = "parakeet-tdt-0.6b-v3";

    // Hotkeys (Plan 1 defaults; persisted as raw VK codes + modifier flags
    // — full chord recording UI comes in Plan 3)
    public string HoldHotkey { get; init; } = "RightCtrl+RightShift";
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+Space";

    // Sound effects
    public bool PlaySounds { get; init; } = true;

    // Plan 3 additions
    public bool AutostartEnabled { get; init; } = false;
    public bool OnboardingCompleted { get; init; } = false;
    public bool SpeakerFilterEnabled { get; init; } = false;
    public string LastVersionSeen { get; init; } = "";
}
