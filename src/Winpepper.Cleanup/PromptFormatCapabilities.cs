namespace Winpepper.Cleanup;

/// <summary>
/// Single source of truth for what a cleanup model's prompt format can
/// actually use. 'chatml' and 'granite' are chat formats whose prompts carry
/// a SYSTEM section (cleanup profile, custom prompt, corrections vocabulary,
/// window context). 'raw-io' is a bare completion format:
/// CleanupPromptFormatter.Build's raw-io arm builds the prompt from ONLY the
/// transcript and structurally discards the system prompt -- empirically
/// confirmed 2026-07-30/31 (the sotto model ignores every in-prompt steering
/// channel). Consumed by both the settings UI (via a delegate wired in
/// AppShell) and PipelineHost's window-context prefetch gate, so the UI and
/// the runtime can never disagree.
/// Unknown/null formats are treated as carrying -- we only claim a setting is
/// ignored when we know the format discards it.
/// </summary>
public static class PromptFormatCapabilities
{
    public static bool CarriesSystemPrompt(string? promptFormat)
        => !string.Equals(promptFormat, CleanupPromptFormatter.RawIo, StringComparison.Ordinal);
}
