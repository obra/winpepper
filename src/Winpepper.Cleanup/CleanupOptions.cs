namespace Winpepper.Cleanup;

/// <summary>
/// Per-session options handed to <c>CleanupRunner.RunAsync</c>. Spec §5.5 + §6.4.
/// </summary>
public sealed record CleanupOptions
{
    /// <summary>
    /// Whether the cleanup LLM runs at all. When false the runner takes the
    /// deterministic corrections-only path (<see cref="CleanupPath.BypassDisabled"/>).
    /// Read live per dictation from <c>AppSettings.CleanupEnabled</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public CleanupProfile Profile { get; init; } = CleanupProfile.Ordinary;

    /// <summary>Custom base prompt; only used when <see cref="Profile"/> is Custom.</summary>
    public string? CustomBasePrompt { get; init; }

    /// <summary>Max time the whole runner is allowed to spend, including pre-warm wait.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Greedy temperature; spec §5.5 says 0.1.</summary>
    public float Temperature { get; init; } = 0.1f;

    /// <summary>Window-context wait budget. Spec §6.1 sets 500 ms.</summary>
    public TimeSpan WindowContextWait { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether to attach window context at all. Default false (Ghost Pepper parity, §6.1).</summary>
    public bool WindowContextEnabled { get; init; }

    /// <summary>Hard cap on output tokens used when the formula in §5.5 would otherwise exceed it.</summary>
    public int MaxNewTokensCap { get; init; } = 2048;
}
