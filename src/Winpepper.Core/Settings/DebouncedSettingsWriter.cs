using Microsoft.Extensions.Logging;

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
    private static readonly System.Reflection.PropertyInfo[] SettingsProperties =
        typeof(AppSettings).GetProperties();

    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private readonly List<Func<AppSettings, AppSettings>> _pendingMutators = new();
    private CancellationTokenSource? _cts;
    private Task? _scheduled;

    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null, ILogger? log = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
        _log = log;
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
        //
        // Log payloads are CAPTURED under the lock; the log CALLS are
        // emitted after it is released — a 250 ms logger sink inside the
        // lock blocked a concurrent Queue() for 236 ms in testing.
        var skippedKeptCount = 0;
        List<Exception>? mutatorErrors = null;
        Exception? saveError = null;
        var requeuedCount = 0;
        AppSettings? loggedBefore = null;
        AppSettings? loggedAfter = null;

        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;

            // Degraded-load guard: if the settings file exists but cannot be
            // READ right now, its fallback value must not become the base of
            // a full-file rewrite — skip this flush and KEEP the pending
            // mutations for retry on the next flush (or the Dispose flush).
            if (!_store.TryLoadCurrent(out var before))
            {
                skippedKeptCount = _pendingMutators.Count;
            }
            else
            {
                var mutators = _pendingMutators.ToArray();
                _pendingMutators.Clear();

                var after = before;
                foreach (var mutator in mutators)
                {
                    // A throwing mutator is DROPPED; the rest of the batch
                    // still applies.
                    try { after = mutator(after); }
                    catch (Exception ex) { (mutatorErrors ??= new()).Add(ex); }
                }

                try
                {
                    _store.Save(after);
                    loggedBefore = before;
                    loggedAfter = after;
                }
                catch (Exception ex)
                {
                    // Save failed (e.g. a Windows sharing violation on the
                    // atomic rename): re-insert the drained batch at the
                    // FRONT so order is preserved and it retries on the next
                    // flush. A Dispose-time flush that fails here gives up
                    // without throwing (the app is exiting).
                    saveError = ex;
                    requeuedCount = mutators.Length;
                    _pendingMutators.InsertRange(0, mutators);
                }
            }
        }

        // All emission OUTSIDE the lock. Counts and exceptions only — never
        // settings values (content-free logging rule).
        if (skippedKeptCount > 0)
            _log?.LogWarning(
                "Settings flush skipped (degraded settings load); keeping {PendingCount} pending mutation(s) for retry",
                skippedKeptCount);
        if (mutatorErrors is not null)
            foreach (var ex in mutatorErrors)
                _log?.LogError(ex,
                    "A queued settings mutator threw and was dropped; remaining mutations were still applied");
        if (saveError is not null)
            _log?.LogWarning(saveError,
                "Settings save failed; re-queued {RequeuedCount} mutation(s) for retry on the next flush",
                requeuedCount);
        if (loggedBefore is not null && loggedAfter is not null)
            LogChangedFields(loggedBefore, loggedAfter);
    }

    private void LogChangedFields(AppSettings before, AppSettings after)
    {
        if (_log is null) return;
        // Reflection diffing is valid for the CURRENT AppSettings: all
        // properties are scalar except InjectionChannels; PropertyValuesEqual
        // sequence-compares string collections so a rebuilt-but-equal list
        // does not mis-diff as changed.
        var changed = SettingsProperties
            .Where(p => !PropertyValuesEqual(p.GetValue(before), p.GetValue(after)))
            .Select(p => p.Name)
            .ToList();
        // Field NAMES only — never values: settings can carry user content
        // (e.g. CleanupCustomPrompt); the repo's content-free logging rule.
        _log.LogInformation(
            "Settings flushed: {ChangedCount} field(s) changed: {ChangedFields}",
            changed.Count,
            changed.Count == 0 ? "(none)" : string.Join(", ", changed));
    }

    /// <summary>Value comparison for the change-diff log. Scalars use
    /// Equals; string collections (InjectionChannels) compare by sequence so
    /// a rebuilt-but-content-equal list does not mis-diff as changed.
    /// Internal for direct unit testing.</summary>
    internal static bool PropertyValuesEqual(object? a, object? b)
    {
        if (a is IEnumerable<string> ea && b is IEnumerable<string> eb)
            return ea.SequenceEqual(eb);
        return Equals(a, b);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
