namespace Winpepper.Core.Settings;

/// <summary>
/// Thread-safe in-memory source of truth for the DESIRED streaming (primary)
/// speech model name — the streaming analog of <see cref="AsrModelSelectionSlot"/>.
/// UI promote callbacks Publish the raw name (persistence to settings.json is
/// durability only); the engine holder Reads it per dictation. Volatile
/// reference: single word publication, last-write-wins.
/// </summary>
public sealed class StreamingModelSelectionSlot
{
    private volatile string? _desired;
    public void Publish(string? modelName) => _desired = modelName;
    public string? Read() => _desired;
}
