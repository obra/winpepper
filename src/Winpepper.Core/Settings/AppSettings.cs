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

    // ASR provider selection
    public string AsrProvider { get; init; } = "local"; // "local" | "assemblyai"
    public string AssemblyAiModel { get; init; } = "universal-2"; // speech_model id sent to AssemblyAI

    // AssemblyAI retention: delete the remote transcript after we have the text
    // so dictated audio/text does not persist on AssemblyAI servers. On by default.
    public bool AssemblyAiDeleteAfterTranscribe { get; init; } = true;

    // Single owned cloud budget (seconds). FallbackTranscriber cancels the cloud
    // attempt after this and falls back to local immediately. Clamped to [5,30].
    public int AssemblyAiCloudDeadlineSeconds { get; init; } = 10;

    // Send Preferred terms as AssemblyAI keyterms_prompt. Off by default: this is
    // a paid add-on on some tiers. Replacements always map to custom_spelling
    // (safe on all tiers) regardless of this flag.
    public bool AssemblyAiKeytermsEnabled { get; init; } = false;

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
