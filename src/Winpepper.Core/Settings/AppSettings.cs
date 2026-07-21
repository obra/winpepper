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

    // Cleanup model selection. Bound to Winpepper.Models.ModelRegistry.DefaultCleanupName.
    public string CleanupModelName { get; init; } = "qwen2.5-0.5b-instruct-q4_k_m";

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

    // Post-paste "offer to learn corrections" prompt. Off by default: this is
    // opt-in behavior (spec Task 5).
    public bool PostPasteLearningEnabled { get; init; } = false;

    // Warm-mic pre-roll: keep one capture stream running so the first ~500 ms
    // of speech is not clipped (Bug 2). On by default; turning it off restores
    // cold-start capture (the mic-in-use indicator then only lights while
    // dictating — a privacy trade-off).
    public bool PrewarmMicEnabled { get; init; } = true;
    public string LastVersionSeen { get; init; } = "";

    // Main-window size in physical pixels; null until the user resizes or the
    // first-run default is applied (spec Task 4). No position persistence.
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
}
