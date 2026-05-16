using Winpepper.Corrections;

namespace Winpepper.History.Lab;

public sealed class CleanupRerunInput
{
    public required string RawTranscript { get; init; }
    public required string ModelName { get; init; }

    /// <summary>
    /// Absolute path to the GGUF file the user picked. The production rerun
    /// service hands this straight to <c>LlamaCleanupBackend</c>'s constructor.
    /// </summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// Override the base prompt for this run. Empty string means "use the
    /// built-in default" — the rerun service maps this to
    /// <c>CleanupProfile.Ordinary</c>; a non-empty string maps to
    /// <c>CleanupProfile.Custom</c> with this text as the base prompt.
    /// </summary>
    public string CustomBasePrompt { get; init; } = "";

    /// <summary>Whether the assembled prompt should include the window-context block.</summary>
    public bool IncludeWindowContext { get; init; }

    /// <summary>
    /// Pre-fetched window context text. The Lab does not refetch on its own;
    /// v1 leaves this empty unless the caller wires a refetch.
    /// </summary>
    public string WindowContextText { get; init; } = "";

    /// <summary>
    /// Corrections data (preferred-transcription hints + misheard-replacement
    /// map) to pass through to <c>CleanupRunner</c>. Empty for an experiment
    /// that ignores user corrections; otherwise the caller loads the live data.
    /// </summary>
    public CorrectionsData Corrections { get; init; } = CorrectionsData.Empty;
}
