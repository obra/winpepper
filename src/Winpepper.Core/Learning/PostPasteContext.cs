namespace Winpepper.Core.Learning;

/// <summary>Snapshot captured at injection completion. Spec §8.2 (1).</summary>
public sealed record PostPasteContext
{
    public required string ElementId { get; init; }
    public required string InjectedText { get; init; }
    public required Guid SessionId { get; init; }
    public required DateTime InjectionEndUtc { get; init; }
}
