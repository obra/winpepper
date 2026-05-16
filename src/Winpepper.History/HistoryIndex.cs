namespace Winpepper.History;

/// <summary>
/// On-disk envelope for the history file. Schema versioned for forward compat.
/// </summary>
public sealed record HistoryIndex
{
    public int Schema { get; init; } = 1;
    public List<HistoryEntry> Entries { get; init; } = new();
}
