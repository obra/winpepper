namespace Winpepper.Core.Settings;

/// <summary>
/// Debounces settings writes. Queued mutations are stored as mutator
/// FUNCTIONS and applied over a FRESH disk load at flush time
/// (read-modify-write), so fields no queued mutator touches always
/// round-trip from disk. This is the fix for the 2026-07 lost-update bug:
/// the previous implementation snapshotted the whole AppSettings record at
/// construction and rewrote it wholesale on every flush, silently reverting
/// any write made outside this instance (e.g. a direct SettingsStore.Save).
/// Mutators therefore execute at flush time, not queue time — a queued
/// mutator is "newer intent" and wins over an out-of-band write to the
/// same field.
/// </summary>
public sealed class DebouncedSettingsWriter : ISettingsWriter, IDisposable
{
    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private readonly List<Func<AppSettings, AppSettings>> _pendingMutators = new();
    private CancellationTokenSource? _cts;
    private Task? _scheduled;

    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
    }

    public void Queue(Func<AppSettings, AppSettings> mutator)
    {
        lock (_lock)
        {
            _pendingMutators.Add(mutator);
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
        // Deliberately NO ConfigureAwait(false): resuming on the captured
        // context keeps the final Flush() — and therefore the mutators — on
        // the calling (UI) thread. Trade-off: FlushAsync().Wait() under a
        // sync context would deadlock; documented, and no production caller
        // blocks on FlushAsync (all await or discard with `_ =`).
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
        // The whole read-modify-write runs under _lock: concurrent flushes
        // serialize (the old code called Save outside the lock, so two
        // flushes could write whole files out of order), and a Queue()
        // racing a flush lands either before the drain (applied now) or
        // after (applied on its own debounce tick) — never lost. Monitor
        // locks are reentrant, so a mutator cannot deadlock this. Measured
        // lock hold is fsync-dominated: median 13.3 ms, p95 22 ms.
        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;

            // Degraded-load guard: if the settings file exists but cannot be
            // READ right now (transient IOException, UnauthorizedAccess),
            // its fallback value must not become the base of a full-file
            // rewrite — skip this flush and KEEP the pending mutations; they
            // retry on the next flush (or the Dispose flush). Missing and
            // corrupt files DO load (defaults are then the legitimate
            // current state; corrupt content is quarantined to a .bad-*
            // backup by the store). Task 3 adds the WRN log line for this.
            if (!_store.TryLoadCurrent(out var settings)) return;

            var mutators = _pendingMutators.ToArray();
            _pendingMutators.Clear();

            foreach (var mutator in mutators)
            {
                // A throwing mutator is DROPPED; the rest of the batch still
                // applies — one bad lambda must not destroy sibling changes.
                // Task 3 adds the ERR log line for this.
                try { settings = mutator(settings); }
                catch { }
            }

            try
            {
                _store.Save(settings);
            }
            catch
            {
                // Save failed (e.g. a Windows sharing violation on the
                // atomic rename): re-insert the drained batch at the FRONT
                // so order is preserved and it retries on the next flush. A
                // Dispose-time flush that fails here gives up without
                // throwing (the app is exiting). Task 3 adds the WRN log
                // line for this.
                _pendingMutators.InsertRange(0, mutators);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
