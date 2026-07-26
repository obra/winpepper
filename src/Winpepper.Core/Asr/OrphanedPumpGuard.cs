namespace Winpepper.Core.Asr;

/// <summary>
/// Serializes disposal of a shared native resource (the ASR ParakeetSession)
/// against ABANDONED streaming pumps that may still be executing a native call
/// on it. A drain-timeout (or teardown) abandon orphans the coordinator's pump
/// task; the next model swap's <c>old?.Dispose()</c> would then be a native
/// use-after-dispose. Callers <see cref="Register"/> each abandoned pump and
/// route every shared-session dispose through <see cref="RunOrDefer"/>: it runs
/// inline when no tracked pump is live, otherwise it is scheduled to run after
/// ALL currently-tracked pumps complete (regardless of success/fault/cancel).
/// Pumps registered AFTER a RunOrDefer snapshot do not gate that dispose — a
/// later pump captured the NEW session, and the next swap's RunOrDefer covers it.
/// </summary>
public sealed class OrphanedPumpGuard
{
    private readonly object _lock = new();
    private readonly List<Task> _pumps = new();
    private readonly Action<Exception>? _onDeferredDisposeError;

    public OrphanedPumpGuard(Action<Exception>? onDeferredDisposeError = null)
        => _onDeferredDisposeError = onDeferredDisposeError;

    /// <summary>Number of tracked pumps that have not yet completed.</summary>
    public int LivePumpCount
    {
        get { lock (_lock) { Prune(); return _pumps.Count; } }
    }

    /// <summary>Track an abandoned pump. Completed tasks are pruned; no-op if
    /// <paramref name="pump"/> is already complete.</summary>
    public void Register(Task pump)
    {
        lock (_lock)
        {
            Prune();
            if (!pump.IsCompleted) _pumps.Add(pump);
        }
    }

    /// <summary>Run <paramref name="dispose"/> now if no tracked pump is live;
    /// otherwise schedule it after ALL currently-tracked pumps complete
    /// (regardless of success/fault/cancel). Never blocks. Exceptions from a
    /// DEFERRED dispose are routed to the constructor's error callback, never
    /// left unobserved; an INLINE dispose throw propagates to the caller,
    /// matching a direct <c>Dispose()</c> call. Caller-side invariant: a pump
    /// must be <see cref="Register"/>ed BEFORE any dispose of the session it
    /// holds is routed through this method — the snapshot only covers
    /// already-registered pumps, so a later-registered pump does not gate an
    /// earlier dispose.</summary>
    public void RunOrDefer(Action dispose)
    {
        Task[] live;
        lock (_lock)
        {
            Prune();
            live = _pumps.ToArray();
        }
        if (live.Length == 0)
        {
            dispose();
            return;
        }
        // WhenAll of faulted/canceled tasks still completes, and an unconditional
        // continuation runs regardless of the antecedent's status. Reading
        // t.Exception marks a faulted antecedent observed (no unobserved-task
        // escalation for pump faults).
        Task.WhenAll(live).ContinueWith(
            t =>
            {
                _ = t.Exception;
                try { dispose(); }
                catch (Exception ex) { _onDeferredDisposeError?.Invoke(ex); }
            },
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void Prune() => _pumps.RemoveAll(t => t.IsCompleted);
}
