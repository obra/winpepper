namespace Winpepper.Asr.Transcription;

/// <summary>One AssemblyAI custom_spelling rule: map misheard forms to the correct text.</summary>
public sealed record AssemblyAiCustomSpelling(IReadOnlyList<string> From, string To);

/// <summary>
/// Optional per-request vocabulary. CustomSpelling is safe on all tiers and is
/// always sent when non-empty. Keyterms maps to keyterms_prompt and is only sent
/// when the user opts in (paid add-on on some tiers). word_boost is intentionally
/// absent: it silently downgrades universal-3 models.
/// </summary>
public sealed record AssemblyAiRequestExtras(
    IReadOnlyList<AssemblyAiCustomSpelling> CustomSpelling,
    IReadOnlyList<string> Keyterms)
{
    public static AssemblyAiRequestExtras Empty { get; } =
        new(Array.Empty<AssemblyAiCustomSpelling>(), Array.Empty<string>());
}
