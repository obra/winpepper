using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Batch (offline, whole-utterance) transcription over the transcribe.cpp
/// engine — the production port of the bench's EngineBatchTranscriber. Serves
/// the StreamingEnabled=false path, the post-worker-restart failure fallback,
/// and every seam that previously required a ParakeetTranscriber.
/// ModelName must NOT equal the streaming model's name: PipelineHost
/// classifies asr_mode=streaming by exact name match, and a batch result must
/// be booked as batch (different latency budget, honest history stamps).
/// With the engine in a worker subprocess there is no compute-gate deadlock:
/// the worker auto-disposes an open stream before a batch, and a wedged
/// worker was already killed before this fallback runs.
/// </summary>
public sealed class NemotronBatchTranscriber : ITranscriber
{
    private readonly Func<ITranscribeCppEngine?> _engineProvider;
    private readonly string? _language;
    private readonly ILogger? _log;

    public NemotronBatchTranscriber(Func<ITranscribeCppEngine?> engineProvider, string modelName,
        string? language = null, ILogger? log = null)
    {
        _engineProvider = engineProvider;
        ModelName = modelName;
        _language = language;
        _log = log;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var engine = _engineProvider()
                ?? throw new InvalidOperationException(
                    "local speech engine unavailable (model not installed or worker restarting)");
            var text = engine.TranscribeBatch(mono16k.ToArray(), _language, out var gateWaitMs);
            if (gateWaitMs > 0)
                _log?.LogInformation("nemotron batch: compute-gate wait {GateWaitMs} ms", gateWaitMs);
            return new TranscriptionResult(text, ModelName);
        }, ct);
}
