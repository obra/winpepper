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
/// </summary>
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;

    public NemotronEngineHolder(string modelsRoot, ILogger log)
    {
        _modelsRoot = modelsRoot;
        _log = log;
    }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            if (!NemotronStreamingModel.IsInstalled(_modelsRoot)) return null;
            return _engine ??= CreateWorkerEngine();
        }
    }

    private ITranscribeCppEngine CreateWorkerEngine()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own executable path for the transcribe worker");
        var factory = new ExeWorkerProcessFactory(
            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker"),
            line => _log.LogWarning("{TranscribeCppLog}", line));
        _log.LogInformation("transcribe.cpp worker engine created ({Model})", NemotronStreamingModel.Name);
        return new WorkerProcessEngine(
            factory,
            NemotronStreamingModel.RuntimeDir(_modelsRoot),
            NemotronStreamingModel.GgufPath(_modelsRoot),
            NemotronStreamingModel.Name,
            log: msg => _log.LogInformation("{WorkerSupervision}", msg));
    }
}
#endif
