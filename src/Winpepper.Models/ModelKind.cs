namespace Winpepper.Models;

public enum ModelKind
{
    Asr,
    Cleanup,
    /// <summary>Streaming ASR engine (transcribe.cpp GGUF + native runtime).
    /// The PRIMARY speech model since 2026-08 (nemotron-first): selected via
    /// AppSettings.StreamingModelName (English default, Multilingual optional),
    /// installed from the onboarding model picker on new installs and by
    /// StreamingAutoInstaller on upgrades. Still never valid as AsrModelName —
    /// that setting names the optional Parakeet batch/backup model.</summary>
    StreamingAsr,
}
