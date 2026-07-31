namespace Winpepper.Cleanup;

/// <summary>
/// Pure launch decision for the window-context prefetch, extracted so
/// PipelineHost's two duplicated hotkey arms share one Linux-tested policy
/// (same pattern as WindowContextStamp / CleanupRunner.Preflight).
/// A raw-io cleanup model discards the system prompt, so gathering window
/// context for it is pure waste (a UIA walk plus waits that can never be
/// consumed) -- no prefetch runs while a raw-io model is active. ctx_src is
/// then omitted from the timing line exactly as when the feature is off.
/// </summary>
public static class WindowContextPrefetchGate
{
    public static bool ShouldPrefetch(
        bool cleanupEnabled, bool windowContextEnabled, string? activePromptFormat)
        => cleanupEnabled
           && windowContextEnabled
           && PromptFormatCapabilities.CarriesSystemPrompt(activePromptFormat);
}
