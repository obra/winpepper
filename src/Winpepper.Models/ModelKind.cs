namespace Winpepper.Models;

public enum ModelKind
{
    Asr,
    Cleanup,
    /// <summary>Streaming-only ASR engine (transcribe.cpp GGUF + native runtime).
    /// Never selectable as AsrModelName; auto-installed in the background on
    /// first run (StreamingAutoInstaller) and installable/repairable from the
    /// Models page, used only when StreamingEnabled and installed.</summary>
    StreamingAsr,
}
