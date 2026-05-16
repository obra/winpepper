namespace Winpepper.History.Lab;

public sealed record CleanupRerunResult
{
    public required string ModelName { get; init; }

    /// <summary>The fully assembled prompt fed to the model. Surfaced in the "Show cleanup transcript" modal.</summary>
    public required string AssembledPrompt { get; init; }

    /// <summary>Raw model output before sanitization (think-tag stripping etc.). Surfaced in the modal.</summary>
    public required string RawOutput { get; init; }

    /// <summary>Final cleaned text shown in the Lab.</summary>
    public required string CleanedText { get; init; }

    public required TimeSpan Elapsed { get; init; }
}
