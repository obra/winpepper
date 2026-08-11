namespace Winpepper.Cleanup;

/// <summary>Outcome of one <c>CleanupRunner</c> invocation.</summary>
public sealed record CleanupResult(
    string CleanedText,
    CleanupPath Path,
    string RawModelOutput,
    string AssembledPrompt,
    TimeSpan Elapsed)
{
    /// <summary>0b consume-time indicator for the timing line's <c>ctx_src</c>.
    /// null = no window-context task supplied / feature disabled (field omitted);
    /// false = a task was supplied but was NOT complete when the runner stopped
    /// waiting (regardless of what it later produced);
    /// true = the task was complete within the bounded wait and its value fed
    /// the prompt build (a faulted-but-complete task still counts — the caller
    /// resolves faults to "none" via IsCompletedSuccessfully).</summary>
    public bool? ConsumedWindowContext { get; init; }

    /// <summary>ms the runner actually waited inside the bounded window-context
    /// wait (CleanupOptions.WindowContextWait); null when no wait ran (no context
    /// task supplied, feature disabled, or bypass before the wait). Consume-time
    /// sibling of <see cref="ConsumedWindowContext"/> — ≈0 once the prefetch
    /// launches at listen-start (kata tbc0).</summary>
    public int? WindowContextWaitMs { get; init; }
}

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
    BypassProvider, // cloud provider already formatted server-side; corrections only
    BypassDisabled, // user turned the cleanup LLM off; corrections only
}
