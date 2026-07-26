#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.App.Hosting;

/// <summary>
/// Process-wide lazy holder for the transcribe.cpp engine. The ~0.9 s model
/// load happens once, on the first streaming dictation after install; the
/// model handle is never freed (no dispose race with in-flight dictations, no
/// OrphanedPumpGuard involvement). Not-installed is re-checked every call so
/// installing the model takes effect without a restart; a LOAD FAILURE latches
/// null for the process lifetime (one loud error, no retry storm).
/// </summary>
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;
    private bool _failedPermanently;

    public NemotronEngineHolder(string modelsRoot, ILogger log)
    {
        _modelsRoot = modelsRoot;
        _log = log;
    }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            if (_engine is not null) return _engine;
            if (_failedPermanently) return null;
            if (!NemotronStreamingModel.IsInstalled(_modelsRoot)) return null;
            try
            {
                _engine = TranscribeCppEngine.Load(
                    NemotronStreamingModel.RuntimeDir(_modelsRoot),
                    NemotronStreamingModel.GgufPath(_modelsRoot),
                    msg => _log.LogWarning("{TranscribeCppLog}", msg));
                _log.LogInformation("transcribe.cpp engine loaded ({Model})", _engine.ModelName);
                return _engine;
            }
            catch (Exception e)
            {
                _failedPermanently = true;
                _log.LogError(e,
                    "transcribe.cpp engine failed to load — local streaming disabled for this run; " +
                    "dictations use batch transcription (contract/ABI/model problem, see exception)");
                return null;
            }
        }
    }
}
#endif
