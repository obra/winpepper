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

    // Accepted aliases: the AssemblyAI API-reference enum spells the premium model
    // "universal-3-5-pro" while pricing/Python-SDK use "universal-3-pro". Map each
    // accepted alias to the listed model id it represents so neither official
    // spelling is wrongly flagged as a "custom" model, and so the picker can select
    // the matching combo item for either spelling.
    private static readonly IReadOnlyDictionary<string, string> KnownAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["universal-3-5-pro"] = "universal-3-pro",
        };

    public static bool IsKnown(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (Known.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
               || KnownAliases.ContainsKey(id));

    /// <summary>
    /// Maps an accepted alias to the listed model id it represents so callers can
    /// resolve either official spelling to a single known id. Returns the input
    /// unchanged when it is already a listed id or an unrecognized (custom) id.
    /// </summary>
    public static string CanonicalId(string id)
        => !string.IsNullOrWhiteSpace(id) && KnownAliases.TryGetValue(id, out var canonical)
            ? canonical
            : id;
}
