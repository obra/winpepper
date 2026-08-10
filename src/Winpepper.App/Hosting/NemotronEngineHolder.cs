#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

namespace Winpepper.App.Hosting;

/// <summary>
/// Process-wide lazy holder for the transcribe.cpp engine, now hosted in a
/// worker SUBPROCESS (Winpepper.exe --transcribe-worker). Not-installed is
/// re-checked every call so installing the model takes effect without a
/// restart. There is NO permanent failure latch anymore: the worker engine's
/// own restart policy (3 consecutive failures -> 60 s cooldown) bounds retry
/// storms, and a wedged or crashed worker recovers on a later dictation.
/// The engine object itself is cheap (the ~0.9 s model load happens inside
/// the worker, lazily, on first use) and is kept for the process lifetime.
/// Selected-model aware (nemotron-first): TryGet resolves the DESIRED layout
/// from the injected selection delegate each call and swaps the worker
/// (dispose old, create new) when the selection changes to a different
/// INSTALLED layout — keep-old-if-new-not-installed, mirroring
/// AsrModelSwapState semantics.
/// </summary>
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly Func<string> _selectedStreamingModelName;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;
    private StreamingModelLayout _currentLayout = StreamingModelLayout.English;

    public NemotronEngineHolder(string modelsRoot, ILogger log, Func<string>? selectedStreamingModelName = null)
    {
        _modelsRoot = modelsRoot;
        _log = log;
        _selectedStreamingModelName = selectedStreamingModelName ?? (() => StreamingModelLayout.English.Name);
    }

    /// <summary>The layout of the engine TryGet would currently serve —
    /// consumers read Language from it for the per-dictation hint.</summary>
    public StreamingModelLayout CurrentLayout { get { lock (_gate) return _currentLayout; } }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            var desired = StreamingModelLayout.For(_selectedStreamingModelName());
            // Swap only when the DESIRED layout differs AND is installed —
            // keep-old-on-missing, mirroring AsrModelSwapState semantics.
            if (desired.Name != _currentLayout.Name && desired.IsInstalled(_modelsRoot))
            {
                _log.LogInformation("streaming model swap: {Old} -> {New} (worker restart)",
                    _currentLayout.Name, desired.Name);
                _engine?.Dispose(); // kills the old worker
                _engine = null;
                _currentLayout = desired;
            }
            if (!_currentLayout.IsInstalled(_modelsRoot))
            {
                // Initial selection may point at a not-yet-installed model
                // while the English one exists (or vice versa) — serve the
                // installed desired target if we have never loaded anything.
                if (_engine is null && desired.IsInstalled(_modelsRoot)) _currentLayout = desired;
                else if (_engine is null) return null;
            }
            return _engine ??= CreateWorkerEngine(_currentLayout);
        }
    }

    private ITranscribeCppEngine CreateWorkerEngine(StreamingModelLayout layout)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own executable path for the transcribe worker");
        var factory = new ExeWorkerProcessFactory(
            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker"),
            line => _log.LogWarning("{TranscribeCppLog}", line));
        _log.LogInformation("transcribe.cpp worker engine created ({Model})", layout.Name);
        return new WorkerProcessEngine(
            factory, layout.RuntimeDir(_modelsRoot), layout.GgufPath(_modelsRoot), layout.Name,
            log: msg => _log.LogInformation("{WorkerSupervision}", msg));
    }
}
#endif
