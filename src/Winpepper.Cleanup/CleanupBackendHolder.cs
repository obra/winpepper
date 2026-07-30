using Microsoft.Extensions.Logging;
using Winpepper.Core.Cleanup;

namespace Winpepper.Cleanup;

/// <summary>
/// Per-dictation snapshot handed to the pipeline: the runner to use for THIS
/// dictation (null = no cleanup model available, fall back to the raw
/// transcript) and the resolved name of the model that runner actually wraps —
/// the value history records must stamp.
/// </summary>
public sealed record CleanupBackendLease(CleanupRunner? Runner, string? LoadedModelName);

/// <summary>
/// Owns the live cleanup backend+runner pair and the machinery for swapping
/// them without an app restart (mirror of the ASR live-swap seam,
/// docs/plans/2026-07-23-live-asr-model-swap.md).
///
/// Contract:
/// - <see cref="RequestPrewarm"/> (UI promote callbacks + boot) starts loading
///   the desired model on a background thread: resolve -> hash-verified
///   readiness (per-file size + SHA-256, injected) -> fresh backend + fresh
///   runner from the NEW model's descriptor values (PromptFormat feeds the
///   backend, OmitPromptExample feeds the runner). It NEVER touches the live
///   pair, so the ~1-1.7s GGUF load is not paid by any dictation.
/// - <see cref="EnsureCurrent"/> (pipeline run loop ONLY, once per dictation
///   at the cleanup seam) adopts a COMPLETED pre-warm and swaps; it never
///   loads synchronously. While a load is still in flight the current pair
///   (possibly none) is kept for this dictation and a later dictation swaps.
///
/// SERIALIZED-CALLER INVARIANT (why disposal is safe without an orphan
/// guard): only PipelineHost's run loop calls EnsureCurrent and
/// CleanupRunner.RunAsync, and the loop awaits RunAsync inline (one hotkey
/// event is fully processed before the next is dequeued). Therefore at the
/// EnsureCurrent seam no generation can be in flight on the old backend, and
/// a pre-warmed backend discarded before ever being handed out has no callers
/// at all. LlamaCleanupBackend.Dispose is NOT gated against concurrent
/// GenerateAsync — this invariant is the safety mechanism. AppShell.Dispose
/// must dispose PipelineHost BEFORE this holder for the same reason.
/// </summary>
public sealed class CleanupBackendHolder : IDisposable
{
    private readonly Func<string?> _desiredModelName;
    private readonly Func<string?, CleanupModelTarget> _resolve;
    private readonly Func<string, bool> _verifyReady;
    private readonly Func<CleanupModelTarget, ILlamaCleanupBackend> _backendFactory;
    private readonly Func<ILlamaCleanupBackend, bool, CleanupRunner> _runnerFactory;
    private readonly ILogger<CleanupBackendHolder> _log;
    private readonly Func<ILlamaCleanupBackend, CancellationToken, Task>? _warmup;

    private readonly object _gate = new();
    private readonly CleanupModelSwapState _swap = new();
    private ILlamaCleanupBackend? _backend;
    private CleanupRunner? _runner;
    private PendingPrewarm? _pending;

    // Prewarm-activity markers for the dictation timing line. Lock-free on
    // purpose: the dictation path must never contend with _gate (Dispose
    // holds it across a bounded 5 s pending-load wait). In-flight count is
    // bumped under _gate in StartPrewarmLocked BEFORE the Task.Run, and
    // dropped in LoadCore's finally; the end-ticks stamp uses
    // Environment.TickCount64 (monotonic, matches PipelineHost's window start).
    private int _prewarmInFlight;
    private long _prewarmLastEndTicks = long.MinValue;

    public CleanupBackendHolder(
        Func<string?> desiredModelName,
        Func<string?, CleanupModelTarget> resolve,
        Func<string, bool> verifyReady,
        Func<CleanupModelTarget, ILlamaCleanupBackend> backendFactory,
        Func<ILlamaCleanupBackend, bool, CleanupRunner> runnerFactory,
        ILogger<CleanupBackendHolder> log,
        Func<ILlamaCleanupBackend, CancellationToken, Task>? warmup = null)
    {
        _desiredModelName = desiredModelName;
        _resolve = resolve;
        _verifyReady = verifyReady;
        _backendFactory = backendFactory;
        _runnerFactory = runnerFactory;
        _log = log;
        _warmup = warmup;
    }

    /// <summary>Resolved name of the currently live model; null until first adoption.</summary>
    public string? LoadedModelName
    {
        get { lock (_gate) return _swap.LoadedModelName; }
    }

    /// <summary>True when a background pre-warm (model load + warm-up
    /// inference) was in flight at any point between
    /// <paramref name="sinceTickCount64"/> (an Environment.TickCount64
    /// reading) and now. Lock-free — safe on the dictation path.</summary>
    public bool WasPrewarmActiveSince(long sinceTickCount64)
    {
        if (Volatile.Read(ref _prewarmInFlight) > 0) return true;
        return Interlocked.Read(ref _prewarmLastEndTicks) >= sinceTickCount64;
    }

    /// <summary>
    /// Start loading the desired model in the background (promote callbacks +
    /// boot). No-op when the desired model is already live or already loading.
    /// </summary>
    public void RequestPrewarm()
    {
        lock (_gate)
        {
            StartPrewarmLocked(_resolve(_desiredModelName()));
        }
    }

    /// <summary>
    /// The per-dictation seam. Adopts a completed pre-warm (swapping and
    /// disposing the replaced backend), never loads synchronously, and returns
    /// the pair to use for THIS dictation.
    /// </summary>
    public CleanupBackendLease EnsureCurrent()
    {
        lock (_gate)
        {
            var target = _resolve(_desiredModelName());
            var pending = _pending;
            var pendingReady = pending is not null
                && string.Equals(pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal)
                && pending.Load is { IsCompletedSuccessfully: true, Result: not null };

            switch (_swap.Plan(target.ResolvedName, pendingReady))
            {
                case CleanupSwapAction.Load:
                case CleanupSwapAction.Swap:
                    var previous = _swap.LoadedModelName;
                    var fresh = pending!.Load.Result!;
                    _pending = null;
                    var old = _backend;
                    _backend = fresh.Backend;
                    _runner = fresh.Runner;
                    _swap.CommitLoad(target.ResolvedName);
                    // Serialized-caller invariant (class doc): no generation is
                    // in flight on the old backend at this seam.
                    DisposeBackend(old);
                    _log.LogInformation(
                        "Cleanup model loaded (swap #{Generation}): {Previous} -> {Model}",
                        _swap.Generation, previous ?? "(none)", target.ResolvedName);
                    break;

                case CleanupSwapAction.KeepCurrent:
                case CleanupSwapAction.CannotStart:
                default:
                    if (!string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
                    {
                        // Desired differs from loaded and no completed pre-warm
                        // exists: kick (or retry) a background load so a later
                        // dictation can swap. No-op while one is in flight.
                        StartPrewarmLocked(target);
                    }
                    else
                    {
                        // Desired == loaded: any pending pre-warm is stale.
                        DiscardPendingLocked();
                    }
                    break;
            }

            return new CleanupBackendLease(_runner, _swap.LoadedModelName);
        }
    }

    private void StartPrewarmLocked(CleanupModelTarget target)
    {
        if (string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
        {
            DiscardPendingLocked(); // desired model already live; drop any stale pre-warm
            return;
        }

        if (_pending is not null
            && string.Equals(_pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal))
        {
            if (!_pending.Load.IsCompleted)
                return; // load in flight
            if (_pending.Load is { IsCompletedSuccessfully: true, Result: not null })
                return; // warm and waiting for the seam
            _pending = null; // completed but failed -> retry below
        }

        DiscardPendingLocked(); // a pre-warm for a different model is stale

        if (target.FellBackToDefault)
        {
            _log.LogWarning(
                "Unknown cleanup model requested; using default {DefaultModel}",
                target.ResolvedName);
        }

        var captured = target;
        Interlocked.Increment(ref _prewarmInFlight); // under _gate, before the task exists — no in-flight gap
        _pending = new PendingPrewarm(captured, Task.Run(() => LoadCore(captured)));
    }

    private PrewarmResult? LoadCore(CleanupModelTarget target)
    {
        try
        {
            // Started INSIDE the try (not before it): a throw here would
            // otherwise escape the method entirely, skipping the finally
            // below and leaking _prewarmInFlight permanently.
            var prewarmSw = System.Diagnostics.Stopwatch.StartNew();
            _log.LogInformation("cleanup prewarm started: {ModelName}", target.ResolvedName);
            if (target.GgufPath is null)
            {
                _log.LogWarning(
                    "Cleanup model {ModelName} declares no .gguf file; keeping the current model.",
                    target.ResolvedName);
                return null;
            }

            // Hash-verified readiness (per-file size + SHA-256) — a merely
            // present-but-stale/corrupt file must never be swapped in. Runs on
            // this background thread, never on the UI thread or the dictation
            // seam, so a cold multi-second hash is safe here.
            if (!_verifyReady(target.ResolvedName))
            {
                _log.LogWarning(
                    "Cleanup model {ModelName} failed verification (missing/size/SHA-256 mismatch); keeping the current model.",
                    target.ResolvedName);
                return null;
            }

            var backend = _backendFactory(target);
            try
            {
                var runner = _runnerFactory(backend, target.OmitPromptExample);
                // Pre-warm is load + WARM-UP: the ~1–1.7 s LoadFromFile figure
                // is load-only; the first generation pays an additional
                // ~0.27–0.49 s (Vulkan shader pipeline + weight paging — ledger
                // A5). Run it here, on the background load task, so a pre-warm
                // is "ready" only when the first real dictation will be fast.
                // A throw is treated as a failed load (backend disposed,
                // retried later); the production delegate
                // (LlamaCleanupBackend.WarmAsync) swallows its own exceptions
                // as non-fatal. Keeping the warm-up inside the load task
                // preserves the disposal discipline: a pending backend is only
                // ever disposed after its load+warm task completes (never with
                // a warm-up in flight — ledger A1b).
                _warmup?.Invoke(backend, CancellationToken.None).GetAwaiter().GetResult();
                _log.LogInformation(
                    "cleanup prewarm finished: {ModelName} in {ElapsedMs} ms (load + warm-up)",
                    target.ResolvedName, (int)prewarmSw.ElapsedMilliseconds);
                return new PrewarmResult(backend, runner);
            }
            catch
            {
                DisposeBackend(backend);
                throw;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Cleanup model {ModelName} failed to load; keeping the current model.",
                target.ResolvedName);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _prewarmLastEndTicks, Environment.TickCount64);
            Interlocked.Decrement(ref _prewarmInFlight);
        }
    }

    private void DisposeBackend(ILlamaCleanupBackend? backend)
    {
        try
        {
            (backend as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cleanup backend dispose failed");
        }
    }

    private void DiscardPendingLocked()
    {
        var pending = _pending;
        if (pending is null) return;
        _pending = null;
        // The pre-warmed backend was never handed out, so no generation can be
        // in flight on it — dispose as soon as its load completes (it may
        // still be running right now).
        pending.Load.ContinueWith(
            t =>
            {
                if (t is { IsCompletedSuccessfully: true, Result: not null })
                    DisposeBackend(t.Result.Backend);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Dispose the live backend. Caller contract: PipelineHost must already be
    /// disposed (run loop stopped) so no dictation holds a lease.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            // Bounded join of an in-flight pre-warm BEFORE discarding: at app
            // exit, ExitProcess terminating a thread mid-native-GGUF-load risks
            // a loader/driver-lock deadlock during DLL_PROCESS_DETACH (ledger
            // A10). A pending load at quit is rare; worst case this waits one
            // load + warm-up. Wait() throwing (faulted/canceled) means the
            // task is terminal — exactly what we need — so it is swallowed.
            try { _pending?.Load.Wait(TimeSpan.FromSeconds(5)); } catch { }
            DiscardPendingLocked();
            DisposeBackend(_backend);
            _backend = null;
            _runner = null;
        }
    }

    private sealed record PendingPrewarm(CleanupModelTarget Target, Task<PrewarmResult?> Load);

    private sealed record PrewarmResult(ILlamaCleanupBackend Backend, CleanupRunner Runner);
}
