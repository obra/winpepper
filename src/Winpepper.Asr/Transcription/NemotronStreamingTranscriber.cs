using System.Diagnostics;
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
    private readonly TimeSpan _nativeCallWarnAfter;

    public NemotronStreamingTranscriber(
        Func<ITranscribeCppEngine> engineProvider,
        ITranscriber batchFallback,
        string modelName,
        ILogger? log = null,
        int attContextRight = 13,
        string? language = null,
        TimeSpan? nativeCallWarnAfter = null)
    {
        _engineProvider = engineProvider;
        _batchFallback = batchFallback;
        ModelName = modelName;
        _log = log;
        _attContextRight = attContextRight;
        _language = language;
        // 3 s: an order of magnitude above a healthy call (feeds are ~tens of
        // ms, finalize ~100-300 ms) yet well below the drain deadline, so a
        // wedge is visible in the log before it becomes a user-facing stall.
        _nativeCallWarnAfter = nativeCallWarnAfter ?? TimeSpan.FromSeconds(3);
    }

    public string ModelName { get; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(
            new Session(_engineProvider, _batchFallback, ModelName, _attContextRight, _language, _log,
                _nativeCallWarnAfter));

    private sealed class Session : IStreamingTranscriptionSession, INativeCallStatsSource
    {
        private readonly Func<ITranscribeCppEngine> _engineProvider;
        private readonly ITranscriber _batchFallback;
        private readonly string _modelName;
        private readonly int _attContextRight;
        private readonly string? _language;
        private readonly ILogger? _log;
        private readonly TimeSpan _nativeCallWarnAfter;

        private readonly float[] _buffer = new float[FeedChunkSamples];
        // Serializes ALL native stream access. The pipeline disposes sessions
        // as a concurrent abort (cancel/silence-drop/drain-timeout/teardown),
        // so Push/Finish/Dispose can genuinely race — never let two of them
        // touch the native stream at once, and never touch it after dispose.
        private readonly object _nativeGate = new();
        private int _buffered;
        private ITranscribeCppStream? _stream;
        private bool _streamed;   // at least one successful native feed
        // Native-call aggregates: mutated in TimedNativeCall's finally, which
        // always runs under _nativeGate (every call site holds it), so plain
        // fields need no interlocking; the snapshot getter takes the gate.
        private int _nativeCalls;
        private long _nativeTotalMs;
        private long _nativeMaxMs;
        private int _nativeOver250;
        internal const int SlowNativeCallMs = 250;
        private readonly List<long> _over250StartTicks = new();
        private int _over250Overflow;
        private bool _corrupt;
        private string? _corruptReason;
        private bool _disposed;

        public Session(Func<ITranscribeCppEngine> engineProvider, ITranscriber batchFallback,
            string modelName, int attContextRight, string? language, ILogger? log,
            TimeSpan nativeCallWarnAfter)
        {
            _engineProvider = engineProvider;
            _batchFallback = batchFallback;
            _modelName = modelName;
            _attContextRight = attContextRight;
            _language = language;
            _log = log;
            _nativeCallWarnAfter = nativeCallWarnAfter;
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
                            TimedNativeCall("stream feed", () => _stream!.Feed(_buffer, FeedChunkSamples));
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
                            TimedNativeCall("stream feed", () => _stream!.Feed(_buffer, _buffered)); // flush the tail
                            _streamed = true;
                            _buffered = 0;
                        }
                        var (text, truncated) = TimedNativeCall("stream finalize", () => _stream!.Finalize());
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
                if (_stream is { } stream) // capture a local: null-state analysis doesn't flow into lambdas (repo builds with WarningsAsErrors=nullable)
                {
                    try { TimedNativeCall("stream dispose", () => { stream.Dispose(); return true; }); }
                    catch { /* native cleanup must not throw upward — preserve existing swallow */ }
                }
                _stream = null;
            }
            return ValueTask.CompletedTask;
        }

        public NativeCallStats NativeCallStats
        {
            get
            {
                lock (_nativeGate)
                {
                    return new NativeCallStats(
                        _nativeCalls, (int)_nativeTotalMs, (int)_nativeMaxMs, _nativeOver250,
                        _over250StartTicks.Count > 0 ? _over250StartTicks.ToArray() : null,
                        _over250Overflow);
                }
            }
        }

        private void EnsureStream()
        {
            if (_stream is not null) return;
            var engine = _engineProvider();
            var startTick = Environment.TickCount64;
            var sw = Stopwatch.StartNew();
            using var watchdogCts = new CancellationTokenSource();
            _ = WarnWhenStillRunningAsync("stream begin", watchdogCts.Token);
            // B4: written by the engine BEFORE the gate-timeout throw (out is
            // by-ref), so the finally sees THIS call's gate wait on both
            // return and throw; 0 if the engine threw before the gate wait.
            var gateWaitMs = 0;
            try
            {
                _stream = engine.BeginStream(_attContextRight, _language, out gateWaitMs);
            }
            finally
            {
                watchdogCts.Cancel();
                sw.Stop();
                // B4: the engine returns THIS call's gate wait per-call (no
                // shared slot to mis-read under overlapping calls); subtract
                // it so native_* stats (and over250_at) measure compute, not
                // queueing behind a prior stream's undisposed session.
                var gateWait = Math.Max(0, gateWaitMs);
                var nativeMs = Math.Max(0, sw.ElapsedMilliseconds - gateWait);
                RecordNativeSample("stream begin", startTick + gateWait, nativeMs);
                if (gateWait > 0)
                    _log?.LogInformation(
                        "stream begin: compute-gate wait {GateWaitMs} ms, native {NativeMs} ms",
                        gateWait, (int)nativeMs);
            }
        }

        /// <summary>Native streaming calls are synchronous P/Invokes that
        /// cannot be cancelled or interrupted; when one wedges, the streaming
        /// pump stalls until it returns and the coordinator's drain deadline
        /// fires (observed: a call stuck >=15 s in the wild). Log twice: an
        /// IN-FLIGHT warning when the threshold crosses while the call is
        /// still stuck (a permanent wedge — or one the user kills the process
        /// over — would otherwise leave zero log evidence, and it also holds
        /// the compute gate, silently batch-degrading every later local
        /// dictation), and the duration on completion, so future wedges are
        /// diagnosable from the log alone.</summary>
        private T TimedNativeCall<T>(string op, Func<T> call)
        {
            var startTick = Environment.TickCount64;
            var nativeSw = Stopwatch.StartNew();
            using var watchdogCts = new CancellationTokenSource();
            _ = WarnWhenStillRunningAsync(op, watchdogCts.Token);
            try { return call(); }
            finally
            {
                watchdogCts.Cancel();
                nativeSw.Stop();
                RecordNativeSample(op, startTick, nativeSw.ElapsedMilliseconds);
            }
        }

        private void RecordNativeSample(string op, long startTick, long elapsedMs)
        {
            _nativeCalls++;
            _nativeTotalMs += elapsedMs;
            if (elapsedMs > _nativeMaxMs) _nativeMaxMs = elapsedMs;
            if (elapsedMs >= SlowNativeCallMs)
            {
                _nativeOver250++;
                if (_over250StartTicks.Count < NativeCallStats.Over250ListCap)
                    _over250StartTicks.Add(startTick);
                else
                    _over250Overflow++;
            }
            if (elapsedMs >= _nativeCallWarnAfter.TotalMilliseconds)
                _log?.LogWarning(
                    "nemotron native {Op} took {ElapsedMs} ms; a call this slow stalls the streaming pump until it returns",
                    op, (int)elapsedMs);
        }

        private async Task WarnWhenStillRunningAsync(string op, CancellationToken ct)
        {
            try { await Task.Delay(_nativeCallWarnAfter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; } // the call completed in time
            _log?.LogWarning(
                "nemotron native {Op} still running after {ThresholdMs} ms; the streaming pump is stalled until it returns",
                op, (int)_nativeCallWarnAfter.TotalMilliseconds);
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
