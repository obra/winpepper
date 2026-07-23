using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Runs a primary (cloud) transcriber under a single owned cloud deadline; on ANY
/// non-user-cancellation failure (including deadline) it falls back to local so the
/// user always gets their dictation. Invalid-model 400s additionally raise a config
/// error so the misconfiguration surfaces persistently instead of degrading silently.
/// </summary>
public sealed class FallbackTranscriber : ITranscriber
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackTranscriber> _log;
    private readonly Action<string>? _onFallback;
    private readonly TimeSpan _cloudDeadline;
    private readonly Action<string>? _onConfigError;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleDeadline;

    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber local,
        ILogger<FallbackTranscriber> logger,
        Action<string>? onFallback = null,
        TimeSpan? cloudDeadline = null,
        Action<string>? onConfigError = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
    {
        _primary = primary;
        _local = local;
        _log = logger;
        _onFallback = onFallback;
        _cloudDeadline = cloudDeadline ?? TimeSpan.FromSeconds(10);
        _onConfigError = onConfigError;
        _scheduleDeadline = scheduleDeadline ?? ((cts, d) => cts.CancelAfter(d));
    }

    public string ModelName => _primary.ModelName;

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        using var cloudCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scheduleDeadline(cloudCts, _cloudDeadline);

        try
        {
            return await _primary.TranscribeAsync(mono16k, cloudCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the USER aborted the dictation — do not run local as well
        }
        catch (Exception ex)
        {
            // Either the cloud deadline fired (cloudCts cancelled, ct not) or the
            // cloud attempt failed. Either way, fall back so the user still gets text.
            if (ex is AssemblyAiException aai && AssemblyAiErrors.IsInvalidModel(aai))
            {
                _log.LogWarning("AssemblyAI model appears invalid; surfacing config error and falling back");
                _onConfigError?.Invoke(aai.Message);
            }
            else
            {
                _log.LogWarning(ex, "Cloud transcription failed or timed out; falling back to local ASR");
            }
            _onFallback?.Invoke(ex.Message);
            return await _local.TranscribeAsync(mono16k, ct);
        }
    }
}
