using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Streaming analog of <see cref="FallbackTranscriber"/>: streams to the cloud
/// during recording; on ANY non-user-cancellation failure (connect, mid-stream
/// push, finish, or the owned cloud deadline on the post-stop wait) it
/// batch-transcribes the full buffer locally so the user always gets their
/// dictation. Invalid-model 400s additionally raise a config error.
/// </summary>
public sealed class FallbackStreamingTranscriber : IStreamingTranscriber
{
    private readonly IStreamingTranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackStreamingTranscriber> _log;
    private readonly Action<string>? _onFallback;
    private readonly TimeSpan _cloudDeadline;
    private readonly Action<string>? _onConfigError;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleDeadline;

    public FallbackStreamingTranscriber(
        IStreamingTranscriber primary,
        ITranscriber local,
        ILogger<FallbackStreamingTranscriber> logger,
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

    public async Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
    {
        IStreamingTranscriptionSession? inner = null;
        Exception? startError = null;
        // Bound the connect with the same cloud deadline that bounds the
        // post-stop wait: on a wedged network ConnectAsync HANGS rather than
        // throws, and PipelineHost's late batch path re-enters here on exactly
        // that network — an unbounded connect would block the serial hotkey loop.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scheduleDeadline(connectCts, _cloudDeadline);
        try
        {
            inner = await _primary.StartSessionAsync(connectCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the USER aborted — do not degrade to a failed-mode session
        }
        catch (Exception ex)
        {
            startError = ex; // includes the connect deadline (connectCts, not ct)
            _log.LogWarning(ex, "Cloud streaming session failed to start or timed out connecting; local fallback will run at stop");
        }
        return new Session(this, inner, startError);
    }

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly FallbackStreamingTranscriber _owner;
        private readonly IStreamingTranscriptionSession? _inner;
        private Exception? _failure;
        private volatile bool _disposed;

        internal Session(FallbackStreamingTranscriber owner, IStreamingTranscriptionSession? inner, Exception? startError)
        {
            _owner = owner;
            _inner = inner;
            _failure = startError;
        }

        public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            // Push-after-dispose is a benign no-op (parity with the nemotron
            // session): the coordinator's pump may legitimately drain queued
            // frames after the pipeline abandoned the dictation. Without this
            // guard the push lands on the DISPOSED inner socket session, whose
            // throw would poison _failure and silently force the local-batch
            // path (plus the user-facing "cloud unavailable" toast) on a
            // lifecycle race rather than a real network failure.
            if (_disposed || _failure is not null || _inner is null) return;
            try
            {
                await _inner.PushAsync(mono16k, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failure = ex;
                _owner._log.LogWarning(ex, "Cloud streaming failed mid-dictation; local fallback will run at stop");
            }
        }

        public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            if (_failure is null && _inner is not null)
            {
                using var cloudCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _owner._scheduleDeadline(cloudCts, _owner._cloudDeadline);
                try
                {
                    return await _inner.FinishAsync(fullAudio, cloudCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // the USER aborted the dictation — do not run local as well
                }
                catch (Exception ex)
                {
                    _failure = ex; // deadline (cloudCts, not ct) or a cloud failure
                }
            }

            var reason = _failure!;
            if (reason is AssemblyAiException aai && AssemblyAiErrors.IsInvalidModel(aai))
            {
                _owner._log.LogWarning("AssemblyAI model appears invalid; surfacing config error and falling back");
                _owner._onConfigError?.Invoke(aai.Message);
            }
            else
            {
                _owner._log.LogWarning(reason, "Cloud streaming failed or timed out; falling back to local ASR");
            }
            _owner._onFallback?.Invoke(reason.Message);
            return await _owner._local.TranscribeAsync(fullAudio, ct);
        }

        public async ValueTask DisposeAsync()
        {
            _disposed = true; // set BEFORE disposing the inner: pushes racing past this point must not reach it
            if (_inner is not null) await _inner.DisposeAsync();
        }
    }
}
