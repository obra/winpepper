namespace Winpepper.Models.ViewModels;

/// <summary>
/// Converts a high-frequency producer stream into bounded UI work. Downloading
/// reports are coalesced per file, while the first/resume value and phase
/// transitions remain ordered and observable.
/// </summary>
internal sealed class DownloadProgressUiBridge
{
    private readonly Action<Action> _dispatch;
    private readonly Action<DownloadProgress> _apply;
    private readonly TimeSpan _updateInterval;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly object _gate = new();
    private readonly Dictionary<ProgressKey, PendingFileProgress> _pending = new();

    private bool _pumpRunning;
    private bool _faulted;
    private Task _drained = Task.CompletedTask;

    public DownloadProgressUiBridge(Action<Action> dispatch,
                                    Action<DownloadProgress> apply,
                                    TimeSpan updateInterval,
                                    Func<TimeSpan, Task>? delay = null)
    {
        _dispatch = dispatch;
        _apply = apply;
        _updateInterval = updateInterval;
        _delay = delay ?? (duration => Task.Delay(duration));
    }

    public void Report(DownloadProgress progress)
    {
        TaskCompletionSource<bool>? cycle = null;
        lock (_gate)
        {
            // Preserve the first pump failure as this run's drain result. A
            // closed dispatcher must not trigger a fresh faulted pump for
            // every subsequent download chunk.
            if (_faulted) return;

            var key = new ProgressKey(progress.DescriptorName, progress.FileRelativePath);
            if (!_pending.TryGetValue(key, out var file))
            {
                file = new PendingFileProgress();
                _pending.Add(key, file);
            }
            file.Add(progress);

            if (!_pumpRunning)
            {
                _pumpRunning = true;
                cycle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _drained = cycle.Task;
            }
        }

        if (cycle is not null)
            _ = PumpAsync(cycle);
    }

    public Task DrainAsync()
    {
        lock (_gate) return _drained;
    }

    public void ResetAfterRun()
    {
        lock (_gate)
        {
            // DrainAsync is the barrier: after it completes there is no queued
            // apply left for this run. Clear high-water state and terminal
            // tombstones so a retry starts a fresh progress generation.
            _pending.Clear();
            _faulted = false;
            _drained = Task.CompletedTask;
        }
    }

    private async Task PumpAsync(TaskCompletionSource<bool> cycle)
    {
        try
        {
            while (true)
            {
                var batch = TakeNextBatch();
                if (batch.Count == 0)
                {
                    if (TryFinish(cycle)) return;
                    continue;
                }

                await DispatchAsync(batch).ConfigureAwait(false);

                if (_updateInterval > TimeSpan.Zero)
                    await _delay(_updateInterval).ConfigureAwait(false);
                else
                    await Task.Yield();

                // Linger for the full interval before ending this cycle. A
                // report arriving shortly after a fast UI apply then joins the
                // same cycle instead of starting another immediate dispatch.
                if (TryFinish(cycle)) return;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _pumpRunning = false;
                _faulted = true;
            }
            cycle.TrySetException(ex);
        }
    }

    private List<DownloadProgress> TakeNextBatch()
    {
        lock (_gate)
        {
            var batch = new List<DownloadProgress>(_pending.Count);

            foreach (var file in _pending.Values)
            {
                if (!file.TryTake(out var progress)) continue;
                batch.Add(progress);
            }

            return batch;
        }
    }

    private bool TryFinish(TaskCompletionSource<bool> cycle)
    {
        lock (_gate)
        {
            if (_pending.Values.Any(file => file.HasPending)) return false;
            _pumpRunning = false;
            cycle.TrySetResult(true);
            return true;
        }
    }

    private Task DispatchAsync(IReadOnlyList<DownloadProgress> batch)
    {
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _dispatch(() =>
            {
                try
                {
                    foreach (var progress in batch) _apply(progress);
                    applied.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    applied.TrySetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            applied.TrySetException(ex);
        }
        return applied.Task;
    }

    private static bool IsTerminal(DownloadPhase phase)
        => phase is DownloadPhase.Complete or DownloadPhase.Failed;

    private readonly record struct ProgressKey(string DescriptorName, string FileRelativePath);

    private sealed class PendingFileProgress
    {
        private readonly Queue<DownloadProgress> _ordered = new();
        private DownloadProgress? _latestDownloading;
        private long _highestDownloaded = -1;
        private bool _seenDownloading;
        private bool _terminalQueued;

        public bool HasPending => _ordered.Count > 0 || _latestDownloading is not null;
        public void Add(DownloadProgress progress)
        {
            // Direct producer callbacks preserve order. Once a terminal state
            // is queued, reject any stale concurrent byte report from the same
            // run so the UI can never regress after Complete/Failed.
            if (_terminalQueued) return;

            if (progress.Phase == DownloadPhase.Downloading)
            {
                if (progress.BytesDownloaded < _highestDownloaded) return;
                _highestDownloaded = progress.BytesDownloaded;

                if (!_seenDownloading)
                {
                    _seenDownloading = true;
                    _ordered.Enqueue(progress);
                }
                else
                {
                    _latestDownloading = progress;
                }
                return;
            }

            if (_latestDownloading is not null)
            {
                _ordered.Enqueue(_latestDownloading);
                _latestDownloading = null;
            }

            _ordered.Enqueue(progress);
            if (IsTerminal(progress.Phase)) _terminalQueued = true;
        }

        public bool TryTake(out DownloadProgress progress)
        {
            if (_ordered.Count > 0)
            {
                progress = _ordered.Dequeue();
                return true;
            }
            if (_latestDownloading is not null)
            {
                progress = _latestDownloading;
                _latestDownloading = null;
                return true;
            }

            progress = null!;
            return false;
        }
    }
}
