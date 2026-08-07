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
    // Default speech model id sent to AssemblyAI (in the plural speech_models
    // array; see AssemblyAiClient). Kept in sync with AssemblyAiModels.DefaultId
    // ("universal-3-5-pro" = Universal-3.5 Pro, latest — a documented, accepted
    // speech_models value and AssemblyAI's own server-side default).
    // No migration for stored values: an existing "universal-2" or "universal-3-pro"
    // is respected as-is and sent over the wire verbatim. NOTE: "universal-3-pro" is
    // a now-deprecated PREDECESSOR model that AssemblyAI itself migrates to
    // "universal-3-5-pro"; if the vendor rejects it at dictation time, the existing
    // invalid-model config-error surfacing + local fallback handle it gracefully.
    public string AssemblyAiModel { get; init; } = "universal-3-5-pro";

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

    // Streaming transcription: transcribe audio while you speak so the text is
    // (nearly) ready the moment you stop. Applies to BOTH providers. Read LIVE
    // per dictation by PipelineHost, so a flip takes effect on the very next
    // dictation. ON BY DEFAULT (2026-07-25): safe in every configuration —
    // AssemblyAI streams with local batch fallback; local WITH the Nemotron
    // streaming model streams via transcribe.cpp with batch fallback on any
    // failure; local WITHOUT it uses BatchStreamingAdapter (identical to the
    // old default: one batch transcription at stop). The chunked-TDT streaming
    // attempt (blank-collapse defect, see 2026-07-25 streaming verification
    // evidence) is no longer wired for local dictations.
    public bool StreamingEnabled { get; init; } = true;

    // Cleanup model selection. Bound to Winpepper.Models.ModelRegistry.DefaultCleanupName.
    public string CleanupModelName { get; init; } = "qwen2.5-0.5b-instruct-q4_k_m";

    // Cleanup LLM settings (Cleanup tab). Persisted here and read LIVE per
    // dictation by PipelineHost, so a toggle flip takes effect on the very next
    // dictation. Defaults mirror CleanupSettingsContract.Defaults().
    public bool CleanupEnabled { get; init; } = true;
    public bool CleanupWindowContextEnabled { get; init; } = false;
    public string CleanupProfile { get; init; } = "Ordinary"; // "Ordinary" | "Literal" | "Custom"
    public string CleanupCustomPrompt { get; init; } = "";
    public int CleanupMaxNewTokens { get; init; } = 512;
    public int CleanupTimeoutMs { get; init; } = 15000;

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

    // Warm-mic pre-roll: keep one capture stream running so the first ~1 s
    // of speech is not clipped (Bug 2). On by default; turning it off restores
    // cold-start capture (the mic-in-use indicator then only lights while
    // dictating — a privacy trade-off).
    public bool PrewarmMicEnabled { get; init; } = true;
    public string LastVersionSeen { get; init; } = "";

    // Main-window size in physical pixels; null until the user resizes or the
    // first-run default is applied (spec Task 4). No position persistence.
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }

    /// <summary>Injection delivery-ladder order (design doc 2026-08-06 §2.3).
    /// Channel names, case-insensitive: "emReplaceSel", "wmCharSmto",
    /// "vkPacket". Unknown names are logged and skipped at parse time; an
    /// empty or fully-invalid list falls back to this hardcoded default.
    /// Parsing lives in Winpepper.Platform.Injection.InjectionChannelNames
    /// (Core cannot reference Platform's DeliveryChannel type). First
    /// collection-typed settings property — DebouncedSettingsWriter's diff
    /// special-cases string sequences for it.</summary>
    public IReadOnlyList<string> InjectionChannels { get; init; } =
        new[] { "emReplaceSel", "wmCharSmto", "vkPacket" };
}
