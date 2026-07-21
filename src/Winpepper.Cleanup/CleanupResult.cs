namespace Winpepper.Cleanup;

/// <summary>Outcome of one <c>CleanupRunner</c> invocation.</summary>
public sealed record CleanupResult(
    string CleanedText,
    CleanupPath Path,
    string RawModelOutput,
    string AssembledPrompt,
    TimeSpan Elapsed);

/// <summary>Which branch the runner took. Surfaced in the History detail later.</summary>
public enum CleanupPath
{
    Llm,                 // The LLM returned usable text after sanitization.
    FallbackEmpty,       // The LLM returned empty/whitespace after sanitization.
    FallbackEllipsis,    // The LLM returned "..." (with or without whitespace).
    FallbackTimeout,     // The 15s timeout fired.
    FallbackBackendError, // The backend threw.
    FallbackImplausible,  // The LLM output echoed prompt scaffolding or blew past plausible length.
    BypassShort,          // Raw transcript under 4 words; LLM skipped, deterministic path taken.
}
