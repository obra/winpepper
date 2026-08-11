using Winpepper.Core.Settings;

namespace Winpepper.History;

/// <summary>Synchronously published runtime history-retention settings.</summary>
public sealed class PublishedHistoryRetentionSlot
{
    private sealed record Snapshot(bool StoreAudio, HistoryRetentionPolicy Policy);

    private readonly object _gate = new();
    private Snapshot _snapshot;

    private PublishedHistoryRetentionSlot(bool storeAudio, HistoryRetentionPolicy policy)
    {
        _snapshot = new Snapshot(storeAudio, policy);
    }

    public bool StoreAudio
    {
        get
        {
            lock (_gate) return _snapshot.StoreAudio;
        }
    }

    public HistoryRetentionPolicy Policy
    {
        get
        {
            lock (_gate) return _snapshot.Policy;
        }
    }

    public (bool StoreAudio, HistoryRetentionPolicy Policy) GetSnapshot()
    {
        lock (_gate)
        {
            var s = _snapshot;
            return (s.StoreAudio, s.Policy);
        }
    }

    public void Publish(bool storeAudio, HistoryRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            _snapshot = new Snapshot(storeAudio, policy);
        }
    }

    public static PublishedHistoryRetentionSlot FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PublishedHistoryRetentionSlot(
            settings.HistoryStoreAudioEnabled,
            HistoryRetentionPolicy.FromSettings(settings));
    }
}
