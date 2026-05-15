using System.Text.Json.Serialization;

namespace Winpepper.Corrections;

/// <summary>
/// Persisted shape of <c>corrections.json</c>. Schema-versioned for forward compat.
/// Spec §8.1.
/// </summary>
public sealed record CorrectionsData
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("preferred")]
    public IReadOnlyList<string> Preferred { get; init; } = Array.Empty<string>();

    [JsonPropertyName("replacements")]
    public IReadOnlyDictionary<string, string> Replacements { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static CorrectionsData Empty { get; } = new();
}
