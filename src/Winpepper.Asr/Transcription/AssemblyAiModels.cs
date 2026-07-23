namespace Winpepper.Asr.Transcription;

/// <summary>Known-good AssemblyAI speech-model ids and their user-facing labels.</summary>
public static class AssemblyAiModels
{
    public readonly record struct ModelChoice(string Id, string Label);

    public static IReadOnlyList<ModelChoice> Known { get; } = new[]
    {
        new ModelChoice("universal-3-5-pro", "Universal-3.5 Pro - latest, most accurate"),
        new ModelChoice("universal-2", "Universal-2 - faster, lower cost"),
    };

    public static string DefaultId => "universal-3-5-pro";

    // Accepted aliases map to the listed model id we want the picker to select.
    // NOTE (verified against AssemblyAI docs, 2026-07): these are display/selection
    // mappings, NOT a claim that the alias and target are the same model server-side.
    //  - "universal-3-pro" is a now-DEPRECATED PREDECESSOR model. AssemblyAI itself
    //    migrates it to "universal-3-5-pro" (active accounts are auto-routed on the
    //    vendor's cutover; new/inactive accounts have "universal-3-pro" rejected with
    //    an error that recommends "universal-3-5-pro"). Aliasing it to our current
    //    listed id matches that vendor migration.
    //  - "best"/"nano" are legacy names AssemblyAI still accepts but routes to the
    //    ACCOUNT-DEFAULT model (not to a fixed tier). We map them to the closest
    //    listed id purely so the picker highlights a real item.
    // Canonicalizing every accepted alias to a listed id lets the settings-page picker
    // always select a real combo item for a stored value (see crash-guard test) and
    // keeps these spellings from being flagged as a "custom" model. The outbound
    // transcription path sends the RAW stored value (no canonicalization), so this
    // table never silently rewrites what goes over the wire.
    private static readonly IReadOnlyDictionary<string, string> KnownAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["universal-3-pro"] = "universal-3-5-pro", // pricing-page spelling -> canonical
            ["best"] = "universal-3-5-pro",            // deprecated alias -> premium tier
            ["nano"] = "universal-2",                  // deprecated alias -> fast tier
        };

    public static bool IsKnown(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (Known.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
               || KnownAliases.ContainsKey(id));

    /// <summary>
    /// Maps an accepted alias to the listed model id it represents so callers can
    /// resolve any accepted spelling to a single listed id. Returns the input
    /// unchanged when it is already a listed id or an unrecognized (custom) id.
    /// </summary>
    public static string CanonicalId(string id)
        => !string.IsNullOrWhiteSpace(id) && KnownAliases.TryGetValue(id, out var canonical)
            ? canonical
            : id;
}
