using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.History.ViewModels;

/// <summary>History recording and retention settings plus their apply state.</summary>
public sealed class HistoryRetentionViewModel : INotifyPropertyChanged
{
    private const int DefaultMaxAgeDays = 30;
    private readonly HistoryStore _store;
    private readonly ISettingsWriter _writer;
    private readonly PublishedHistoryRetentionSlot _slot;
    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private bool _storeAudioEnabled;
    private double _maxEntries;
    private double _maxAgeDays;
    private bool _keepForever;
    private string _diskUsageDisplay;
    private bool _lastCommitPersisted = true;
    private bool _lastApplyHadIndexFailure;
    private int _lastApplyRetainedAfterFailedDelete;

    public HistoryRetentionViewModel(
        HistoryStore store,
        ISettingsWriter writer,
        PublishedHistoryRetentionSlot slot)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(slot);

        _store = store;
        _writer = writer;
        _slot = slot;
        var (storeAudio, policy) = slot.GetSnapshot();
        _storeAudioEnabled = storeAudio;
        _maxEntries = Math.Clamp(policy.MaxEntries, 1, 10_000);
        _keepForever = policy.MaxAgeDays is null;
        _maxAgeDays = Math.Clamp(
            policy.MaxAgeDays ?? DefaultMaxAgeDays, 1, 36_500);
        _diskUsageDisplay = "Saved audio: scanning…";
        _ = InitializeUsageAsync();
    }

    public bool StoreAudioEnabled
    {
        get => _storeAudioEnabled;
        set
        {
            if (_storeAudioEnabled == value) return;
            _storeAudioEnabled = value;
            OnPropertyChanged();
            PublishAndCommit(
                s => s with { HistoryStoreAudioEnabled = value },
                (slot, _) => slot.PublishAudio(value));
        }
    }

    public double MaxEntries
    {
        get => _maxEntries;
        set
        {
            if (double.IsNaN(value)) return;
            var clamped = ClampWholeNumber(value, 1, 10_000);
            if (_maxEntries == clamped) return;
            _maxEntries = clamped;
            OnPropertyChanged();
            var committed = (int)clamped;
            PublishAndCommit(
                s => s with { HistoryMaxEntries = committed },
                static (slot, policy) => slot.PublishPolicy(policy));
        }
    }

    public double MaxAgeDays
    {
        get => _maxAgeDays;
        set
        {
            if (double.IsNaN(value)) return;
            var clamped = ClampWholeNumber(value, 1, 36_500);
            if (_maxAgeDays == clamped) return;
            _maxAgeDays = clamped;
            OnPropertyChanged();
            var committed = _keepForever ? (int?)null : (int)clamped;
            PublishAndCommit(
                s => s with { HistoryMaxAgeDays = committed },
                static (slot, policy) => slot.PublishPolicy(policy));
        }
    }

    public bool KeepForever
    {
        get => _keepForever;
        set
        {
            if (_keepForever == value) return;
            _keepForever = value;
            OnPropertyChanged();
            var committed = value ? (int?)null : (int)_maxAgeDays;
            PublishAndCommit(
                s => s with { HistoryMaxAgeDays = committed },
                static (slot, policy) => slot.PublishPolicy(policy));
        }
    }

    public string DiskUsageDisplay
    {
        get => _diskUsageDisplay;
        private set
        {
            if (_diskUsageDisplay == value) return;
            _diskUsageDisplay = value;
            OnPropertyChanged();
        }
    }

    public bool LastCommitPersisted
    {
        get => _lastCommitPersisted;
        private set
        {
            if (_lastCommitPersisted == value) return;
            _lastCommitPersisted = value;
            OnPropertyChanged();
        }
    }

    public bool LastApplyHadIndexFailure
    {
        get => _lastApplyHadIndexFailure;
        private set
        {
            if (_lastApplyHadIndexFailure == value) return;
            _lastApplyHadIndexFailure = value;
            OnPropertyChanged();
        }
    }

    public int LastApplyRetainedAfterFailedDelete
    {
        get => _lastApplyRetainedAfterFailedDelete;
        private set
        {
            if (_lastApplyRetainedAfterFailedDelete == value) return;
            _lastApplyRetainedAfterFailedDelete = value;
            OnPropertyChanged();
        }
    }

    public event EventHandler? RetentionApplied;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh()
        => DiskUsageDisplay = FormatDiskUsage(_store.ComputeAudioDiskUsageBytes());

    public async Task<HistoryAudioCleanupResult> DeleteAllAudioAsync()
    {
        var result = await Task.Run(_store.DeleteAllAudio);
        var display = await Task.Run(
            () => FormatDiskUsage(_store.ComputeAudioDiskUsageBytes()));
        LastApplyHadIndexFailure = result.IndexSaveFailed || result.EnumerationFailed;
        DiskUsageDisplay = display;
        RetentionApplied?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private async Task InitializeUsageAsync()
    {
        var display = await Task.Run(
            () => FormatDiskUsage(_store.ComputeAudioDiskUsageBytes()));
        DiskUsageDisplay = display;
    }

    private void PublishAndCommit(
        Func<AppSettings, AppSettings> mutator,
        Action<PublishedHistoryRetentionSlot, HistoryRetentionPolicy> publish)
    {
        var committedPolicy = CurrentPolicy();
        publish(_slot, committedPolicy);

        try
        {
            var flush = _writer.TryQueueAndFlushAsync(mutator);
            _ = CommitAndApplyAsync(flush, committedPolicy);
        }
        catch
        {
            LastCommitPersisted = false;
        }
    }

    private async Task CommitAndApplyAsync(
        Task<bool> flush,
        HistoryRetentionPolicy committedPolicy)
    {
        await _applyGate.WaitAsync();
        LastApplyHadIndexFailure = false;
        LastApplyRetainedAfterFailedDelete = 0;
        try
        {
            LastCommitPersisted = await flush;
            var prune = await Task.Run(() => _store.Prune(committedPolicy));
            LastApplyHadIndexFailure = prune.IndexSaveFailed ||
                                       prune.LoadFailed;
            LastApplyRetainedAfterFailedDelete = prune.RetainedAfterFailedDelete;
            var display = await Task.Run(
                () => FormatDiskUsage(_store.ComputeAudioDiskUsageBytes()));
            DiskUsageDisplay = display;
            RetentionApplied?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Fire-and-forget apply chains must never surface an unobserved
            // exception into the UI synchronization context.
            LastCommitPersisted = false;
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private HistoryRetentionPolicy CurrentPolicy()
        => new()
        {
            MaxEntries = (int)_maxEntries,
            MaxAgeDays = _keepForever ? null : (int)_maxAgeDays,
        };

    private static double ClampWholeNumber(double value, int minimum, int maximum)
        => Math.Clamp(Math.Round(value), minimum, maximum);

    private static string FormatDiskUsage(long bytes)
        => $"Saved audio: {bytes} bytes";

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
