namespace Winpepper.Models;

public enum ModelKind
{
    Asr,
    Cleanup,
    /// <summary>Streaming-only ASR engine (transcribe.cpp GGUF + native runtime).
    /// Never selectable as AsrModelName; opt-in install, used only when
    /// StreamingEnabled and installed.</summary>
    StreamingAsr,
}
