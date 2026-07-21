namespace Winpepper.Asr.Transcription;

/// <summary>Known-good AssemblyAI speech-model ids and their user-facing labels.</summary>
public static class AssemblyAiModels
{
    public readonly record struct ModelChoice(string Id, string Label);

    public static IReadOnlyList<ModelChoice> Known { get; } = new[]
    {
        new ModelChoice("universal-2", "universal-2 (fast)"),
        new ModelChoice("universal-3-pro", "universal-3-pro (premium)"),
    };

    public static string DefaultId => "universal-2";

    // Accepted alias: the AssemblyAI API-reference enum spells the premium model
    // "universal-3-5-pro" while pricing/Python-SDK use "universal-3-pro". Recognize
    // both so neither official spelling is wrongly flagged as a "custom" model.
    private static readonly string[] KnownAliases = { "universal-3-5-pro" };

    public static bool IsKnown(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (Known.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
               || KnownAliases.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)));
}
