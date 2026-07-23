namespace Winpepper.Core.Settings;

public sealed class DebouncedSettingsWriter : ISettingsWriter, IDisposable
{
    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private AppSettings _pending;
    private bool _dirty;
    private CancellationTokenSource? _cts;
    private Task? _scheduled;

    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
        _pending = store.Load();
    }

    public void Queue(Func<AppSettings, AppSettings> mutator)
    {
        lock (_lock)
        {
            _pending = mutator(_pending);
            _dirty = true;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _scheduled = Task.Run(async () =>
            {
                try { await Task.Delay(_delay, token); }
                catch (OperationCanceledException) { return; }
                Flush();
            });
        }
    }

    public async Task FlushAsync()
    {
        Task? t;
        lock (_lock) { t = _scheduled; _cts?.Cancel(); }
        if (t is not null) { try { await t; } catch { } }
        Flush();
    }

    public async Task QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        Queue(mutator);
        await FlushAsync();
    }

    private void Flush()
    {
        AppSettings? toWrite = null;
        lock (_lock)
        {
            if (_dirty) { toWrite = _pending; _dirty = false; }
        }
        if (toWrite is not null) _store.Save(toWrite);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
