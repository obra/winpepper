using Winpepper.Core.Settings;

namespace Winpepper.History;

/// <summary>Synchronously published runtime history-retention settings.</summary>
public sealed class PublishedHistoryRetentionSlot
{
    private sealed record Snapshot(bool StoreAudio, HistoryRetentionPolicy Policy);

    private readonly object _gate = new();
    private Snapshot _snapshot;
    private long _commitSequence;

    private PublishedHistoryRetentionSlot(bool storeAudio, HistoryRetentionPolicy policy)
    {
        _snapshot = new Snapshot(storeAudio, policy);
    }

    /// <summary>
    /// Monotonic commit ordering shared across every consumer view-model LIFETIME (pages
    /// re-create VMs on navigation; abandoned apply chains continue in the background).
    /// A retention chain captures its sequence at commit time and must skip destructive
    /// work when the slot has since accepted a newer commit.
    /// </summary>
    public long NextCommitSequence() => Interlocked.Increment(ref _commitSequence);

    /// <summary>The latest commit sequence accepted by this slot.</summary>
    public long CurrentCommitSequence => Interlocked.Read(ref _commitSequence);

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

    public void PublishAudio(bool storeAudio)
    {
        lock (_gate)
        {
            _snapshot = new Snapshot(storeAudio, _snapshot.Policy);
        }
    }

    public void PublishPolicy(HistoryRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            _snapshot = new Snapshot(_snapshot.StoreAudio, policy);
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
