using Winpepper.Corrections;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Maps the user's corrections vocabulary into AssemblyAI request extras.
/// Replacements always become custom_spelling (safe on all tiers). Preferred
/// terms become keyterms_prompt only when the caller opts in (paid on some tiers).
/// </summary>
public static class CorrectionSpellingMapper
{
    public static AssemblyAiRequestExtras ToExtras(CorrectionsData data, bool includeKeyterms)
    {
        var spelling = data.Replacements
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => new AssemblyAiCustomSpelling(new[] { kv.Key }, kv.Value))
            .ToArray();

        var keyterms = includeKeyterms
            ? data.Preferred.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray()
            : Array.Empty<string>();

        if (spelling.Length == 0 && keyterms.Length == 0) return AssemblyAiRequestExtras.Empty;
        return new AssemblyAiRequestExtras(spelling, keyterms);
    }
}
