using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Real local streaming over transcribe.cpp + nemotron-speech-streaming.
/// One native stream per dictation; feeds 160 ms chunks; committed text grows
/// append-only during speech; finalize at stop returns the final transcript
/// (~100-300 ms). PRESERVES the blank-collapse-era guard posture: ANY engine
/// failure, a truncated stream, an empty final transcript, or a zero-push
/// session falls back to the TDT ONNX batch transcriber with a loud warning.
/// The engine (loaded model) is owned by the caller (NemotronEngineHolder);
/// this class disposes only its per-dictation stream.
/// </summary>
public sealed class NemotronStreamingTranscriber : IStreamingTranscriber
{
    /// <summary>160 ms at 16 kHz — the spike's proven feed size (RTF 0.112 at R=13).</summary>
    internal const int FeedChunkSamples = 2560;

    private readonly Func<ITranscribeCppEngine> _engineProvider;
    private readonly ITranscriber _batchFallback;
    private readonly ILogger? _log;
    private readonly int _attContextRight;
    private readonly string? _language;

    public NemotronStreamingTranscriber(
        Func<ITranscribeCppEngine> engineProvider,
        ITranscriber batchFallback,
        string modelName,
        ILogger? log = null,
        int attContextRight = 13,
        string? language = null)
    {
        _engineProvider = engineProvider;
        _batchFallback = batchFallback;
        ModelName = modelName;
        _log = log;
        _attContextRight = attContextRight;
        _language = language;
    }

    public string ModelName { get; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(
            new Session(_engineProvider, _batchFallback, ModelName, _attContextRight, _language, _log));

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly Func<ITranscribeCppEngine> _engineProvider;
        private readonly ITranscriber _batchFallback;
        private readonly string _modelName;
        private readonly int _attContextRight;
        private readonly string? _language;
        private readonly ILogger? _log;

        private readonly float[] _buffer = new float[FeedChunkSamples];
        // Serializes ALL native stream access. The pipeline disposes sessions
        // as a concurrent abort (cancel/silence-drop/drain-timeout/teardown),
        // so Push/Finish/Dispose can genuinely race — never let two of them
        // touch the native stream at once, and never touch it after dispose.
        private readonly object _nativeGate = new();
        private int _buffered;
        private ITranscribeCppStream? _stream;
        private bool _streamed;   // at least one successful native feed
        private bool _corrupt;
        private string? _corruptReason;
        private bool _disposed;

        public Session(Func<ITranscribeCppEngine> engineProvider, ITranscriber batchFallback,
            string modelName, int attContextRight, string? language, ILogger? log)
        {
            _engineProvider = engineProvider;
            _batchFallback = batchFallback;
            _modelName = modelName;
            _attContextRight = attContextRight;
            _language = language;
            _log = log;
        }

        public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_nativeGate)
            {
                if (_disposed || _corrupt) return ValueTask.CompletedTask;
                try
                {
                    EnsureStream();
                    var span = mono16k.Span;
                    var offset = 0;
                    while (offset < span.Length)
                    {
                        var take = Math.Min(FeedChunkSamples - _buffered, span.Length - offset);
                        span.Slice(offset, take).CopyTo(_buffer.AsSpan(_buffered));
                        _buffered += take;
                        offset += take;
                        if (_buffered == FeedChunkSamples)
                        {
                            _stream!.Feed(_buffer, FeedChunkSamples);
                            _streamed = true;
                            _buffered = 0;
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    MarkCorrupt("push", e);
                }
            }
            return ValueTask.CompletedTask;
        }

        public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            // All native work happens synchronously under the lock; the batch
            // fallback await runs OUTSIDE it (never hold a lock across await).
            string? fallbackReason;
            string finalText = "";
            lock (_nativeGate)
            {
                if (_disposed)
                {
                    fallbackReason = "session was disposed (aborted) before finish";
                }
                else if (!_corrupt && _stream is null && _buffered == 0)
                {
                    // Zero pushed audio (streaming-off "late path", or
                    // all-silence recordings) — no native stream at all.
                    fallbackReason = "no audio was streamed";
                }
                else if (_corrupt)
                {
                    fallbackReason = _corruptReason ?? "streaming failed";
                }
                else
                {
                    try
                    {
                        EnsureStream();
                        if (_buffered > 0)
                        {
                            _stream!.Feed(_buffer, _buffered);   // flush the tail
                            _streamed = true;
                            _buffered = 0;
                        }
                        var (text, truncated) = _stream!.Finalize();
                        if (truncated)
                            fallbackReason = "stream reports was_truncated";
                        else if (string.IsNullOrWhiteSpace(text))
                            fallbackReason = "final streamed transcript is empty";
                        else if (!_streamed)
                            fallbackReason = "no chunk was ever fed";
                        else
                        {
                            fallbackReason = null;
                            finalText = text;
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        MarkCorrupt("finish", e);
                        fallbackReason = _corruptReason!;
                    }
                }
            }

            if (fallbackReason is not null)
                return await Fallback(fallbackReason, fullAudio, ct).ConfigureAwait(false);
            return new TranscriptionResult(finalText, _modelName);
        }

        public ValueTask DisposeAsync()
        {
            lock (_nativeGate)
            {
                _disposed = true;   // Push/Finish become no-ops / batch-only after this
                try { _stream?.Dispose(); } catch { /* native cleanup must not throw upward */ }
                _stream = null;
            }
            return ValueTask.CompletedTask;
        }

        private void EnsureStream()
        {
            _stream ??= _engineProvider().BeginStream(_attContextRight, _language);
        }

        private void MarkCorrupt(string where, Exception e)
        {
            _corrupt = true;
            _corruptReason = $"{where}: {e.Message}";
            _log?.LogWarning(e, "nemotron streaming failed during {Where}; will fall back to batch", where);
        }

        private async Task<TranscriptionResult> Fallback(string reason, ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            _log?.LogWarning(
                "nemotron streaming fell back to batch transcription: {Reason}. " +
                "Streamed latency win lost for this dictation; transcript comes from the ONNX batch engine.",
                reason);
            return await _batchFallback.TranscribeAsync(fullAudio, ct).ConfigureAwait(false);
        }
    }
}
