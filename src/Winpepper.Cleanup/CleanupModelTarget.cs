namespace Winpepper.Cleanup;

/// <summary>
/// Everything the cleanup-backend holder needs to know about a resolved
/// cleanup model. Field-for-field mirror of
/// <c>Winpepper.Models.CleanupModelResolution</c>, re-declared here because
/// Winpepper.Cleanup deliberately does not reference Winpepper.Models (the
/// established decoupling — History.Lab's rerun service passes plain values
/// too). AppShell maps between the two records.
/// </summary>
public sealed record CleanupModelTarget(
    string? GgufPath,
    string ResolvedName,
    bool FellBackToDefault,
    string PromptFormat,
    bool OmitPromptExample = false);
