using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>Starts one ParakeetStreamingSession per dictation over the shared
/// local backend (ParakeetSession implements IParakeetBackend).</summary>
public sealed class ParakeetStreamingTranscriber : IStreamingTranscriber
{
    private readonly IParakeetBackend _backend;
    private readonly ITranscriber _batchFallback;
    private readonly PreprocessorConfig _config;
    private readonly ILogger? _log;

    public ParakeetStreamingTranscriber(
        IParakeetBackend backend,
        ITranscriber batchFallback,
        string modelName,
        PreprocessorConfig config,
        ILogger? log = null)
    {
        _backend = backend;
        _batchFallback = batchFallback;
        ModelName = modelName;
        _config = config;
        _log = log;
    }

    public string ModelName { get; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(new ParakeetStreamingSession(
            _backend, ModelName, _config,
            (audio, ct2) => _batchFallback.TranscribeAsync(audio, ct2),
            log: _log));
}
