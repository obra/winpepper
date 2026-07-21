using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Runs a primary (cloud) transcriber; on ANY non-cancellation failure it
/// transparently falls back to the local transcriber so the user always gets
/// their dictation. The returned result's ProviderModelName reflects whichever
/// provider actually produced the text.
/// </summary>
public sealed class FallbackTranscriber : ITranscriber
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackTranscriber> _log;
    private readonly Action<string>? _onFallback;

    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber local,
        ILogger<FallbackTranscriber> logger,
        Action<string>? onFallback = null)
    {
        _primary = primary;
        _local = local;
        _log = logger;
        _onFallback = onFallback;
    }

    public string ModelName => _primary.ModelName;

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        try
        {
            return await _primary.TranscribeAsync(mono16k, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user aborted the dictation — do not run local as well
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloud transcription failed; falling back to local ASR");
            _onFallback?.Invoke(ex.Message);
            return await _local.TranscribeAsync(mono16k, ct);
        }
    }
}
