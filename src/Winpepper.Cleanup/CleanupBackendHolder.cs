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

    private readonly object _gate = new();
    private readonly CleanupModelSwapState _swap = new();
    private ILlamaCleanupBackend? _backend;
    private CleanupRunner? _runner;
    private PendingPrewarm? _pending;

    public CleanupBackendHolder(
        Func<string?> desiredModelName,
        Func<string?, CleanupModelTarget> resolve,
        Func<string, bool> verifyReady,
        Func<CleanupModelTarget, ILlamaCleanupBackend> backendFactory,
        Func<ILlamaCleanupBackend, bool, CleanupRunner> runnerFactory,
        ILogger<CleanupBackendHolder> log)
    {
        _desiredModelName = desiredModelName;
        _resolve = resolve;
        _verifyReady = verifyReady;
        _backendFactory = backendFactory;
        _runnerFactory = runnerFactory;
        _log = log;
    }

    /// <summary>Resolved name of the currently live model; null until first adoption.</summary>
    public string? LoadedModelName
    {
        get { lock (_gate) return _swap.LoadedModelName; }
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
                    break;
            }

            return new CleanupBackendLease(_runner, _swap.LoadedModelName);
        }
    }

    private void StartPrewarmLocked(CleanupModelTarget target)
    {
        if (string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
            return; // the desired model is already live

        if (_pending is not null
            && string.Equals(_pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal))
        {
            return; // already loading (or loaded and waiting for the seam)
        }

        var captured = target;
        _pending = new PendingPrewarm(captured, Task.Run(() => LoadCore(captured)));
    }

    private PrewarmResult? LoadCore(CleanupModelTarget target)
    {
        try
        {
            var backend = _backendFactory(target);
            try
            {
                var runner = _runnerFactory(backend, target.OmitPromptExample);
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

    /// <summary>
    /// Dispose the live backend. Caller contract: PipelineHost must already be
    /// disposed (run loop stopped) so no dictation holds a lease.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            DisposeBackend(_backend);
            _backend = null;
            _runner = null;
        }
    }

    private sealed record PendingPrewarm(CleanupModelTarget Target, Task<PrewarmResult?> Load);

    private sealed record PrewarmResult(ILlamaCleanupBackend Backend, CleanupRunner Runner);
}
