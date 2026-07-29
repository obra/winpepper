using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>Out-of-band per-finish metrics, mirroring the DrainTimedOut /
/// PumpCompletion precedent: set by FinishAsync on EVERY path — including the
/// drain-timeout abandon, where no TranscriptionResult exists and a
/// result-attached metadata slot could never carry them. AsrWaitMs is the
/// pump wait (_pump.WaitAsync) — usually the backlog drain, but it also spans
/// session STARTUP work (cold factory/model load, cloud connect) when the
/// session was still starting at stop, so read it with backlog_ms.
/// AsrNativeMs spans the inner
/// session's FinishAsync — tail feed + finalize on the streaming happy path;
/// includes batch-fallback time when the transcriber falls back internally
/// (asr_mode=batch on the timing line reveals that case). Backlog is what was
/// queued-but-not-yet-pumped at finish entry (frames, and samples/16 as ms —
/// samples because the pre-roll frame is oversized).</summary>
public sealed record StreamingFinishStats(
    int AsrWaitMs,
    int? AsrNativeMs,
    int BacklogFrames,
    int BacklogMs,
    NativeCallStats? NativeCallStats);

/// <summary>
/// Per-dictation glue between the audio frame event and a streaming session.
/// Frames are copied into an unbounded channel on the capture thread (never
/// blocking it) and pumped into the session on a background task. The session
/// may not exist yet when the first frames arrive — the transcriber factory
/// (model ensure + build) runs concurrently on the pump — so frames queue until
/// it is ready. FinishAsync completes the pump and returns the final transcript,
/// or null when no transcriber materialized (caller uses the batch-adapter path).
/// The pump drain is bounded by a drain deadline (default 10 s): a wedged push
/// HANGS rather than throws (half-dead socket send, or a stuck synchronous
/// native P/Invoke), so on timeout FinishAsync abandons the session and
/// returns null promptly — the caller's batch path takes over. The abandoned
/// session's dispose runs in the BACKGROUND: disposing a socket session aborts
/// the socket (which unwedges its pump), but disposing a native session cannot
/// interrupt an in-flight P/Invoke — it only prevents further use and frees
/// native state once the call returns — so no caller-facing path ever awaits it.
/// Invariant: callers never invoke FinishAsync/DisposeAsync concurrently
/// (PipelineHost enforces this via grab-and-null ownership transfer), which is
/// what the remaining plain <c>_session</c> reads rely on.
/// </summary>
public sealed class StreamingDictationSession : IAsyncDisposable
{
    private readonly Channel<PooledFrame> _frames = Channel.CreateUnbounded<PooledFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private readonly ILogger _log;
    private readonly TimeSpan _drainDeadline;
    private IStreamingTranscriptionSession? _session;
    private Exception? _pumpError;

    private const int SamplesPerMs = 16; // mono 16 kHz

    // Queue-depth counters: incremented on successful TryWrite (capture
    // thread), decremented at every dequeue site (pump thread). Interlocked
    // because writer and reader are different threads; read once at finish.
    private int _queuedFrames;
    private long _queuedSamples;

    /// <summary>Drain bound applied when ZERO pushes completed by stop time.
    /// In that state streaming has no latency win to preserve: the engine
    /// would have to process ALL queued audio during the drain anyway, and the
    /// caller's late batch path on fullAudio produces an equivalent transcript
    /// (the session itself logs "Streamed latency win lost" when it falls
    /// back) — so waiting the full deadline buys the user nothing. Applies
    /// ONLY once the session actually started: a session still starting at
    /// stop time (cloud connect is designed-bounded at 10 s; a cold factory
    /// load takes seconds) keeps the full deadline — abandoning it early would
    /// trade a healthy dictation for a local batch one.</summary>
    private static readonly TimeSpan ZeroPushDrainDeadline = TimeSpan.FromSeconds(1.5);

    private volatile bool _sessionStarted;   // written by the pump, read by FinishAsync
    private volatile bool _anyPushCompleted; // written by the pump, read by FinishAsync

    /// <summary>A rented buffer + its real length (ArrayPool rounds up).
    /// Ownership: OnFrame rents; whichever dequeue site consumes it returns it.
    /// Frames never dequeued (channel dropped with the object) are simply
    /// collected — ArrayPool does not require returns for correctness.</summary>
    private readonly struct PooledFrame
    {
        public PooledFrame(float[] buffer, int length) { Buffer = buffer; Length = length; }
        public float[] Buffer { get; }
        public int Length { get; }
        public ReadOnlyMemory<float> Memory => Buffer.AsMemory(0, Length);
    }

    private StreamingDictationSession(
        Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
        ILogger log,
        CancellationToken ct,
        TimeSpan? drainDeadline)
    {
        _log = log;
        _drainDeadline = drainDeadline ?? TimeSpan.FromSeconds(10);
        _pump = Task.Run(async () =>
        {
            try
            {
                var transcriber = await transcriberFactory(ct);
                if (transcriber is null)
                {
                    // No provider available: drain and drop so nothing accumulates.
                    await foreach (var dropped in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    {
                        Interlocked.Decrement(ref _queuedFrames);
                        Interlocked.Add(ref _queuedSamples, -dropped.Length);
                        ArrayPool<float>.Shared.Return(dropped.Buffer);
                    }
                    return;
                }
                var session = await transcriber.StartSessionAsync(ct);
                _session = session;
                _sessionStarted = true; // keys FinishAsync's drain-deadline choice (with _anyPushCompleted)
                // Push via the LOCAL reference, never the nullable field: an
                // abandon (silence-drop / cancel / drain timeout) nulls
                // _session concurrently with this loop, and completing the
                // writer does not stop ReadAllAsync from yielding frames that
                // are already queued. Pushing into a disposed session is a
                // benign no-op by session contract.
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -frame.Length);
                    try
                    {
                        await session.PushAsync(frame.Memory, ct);
                    }
                    finally
                    {
                        // Safe: every PushAsync implementation consumes the
                        // samples before its ValueTask completes (contract on
                        // IStreamingTranscriptionSession.PushAsync).
                        ArrayPool<float>.Shared.Return(frame.Buffer);
                    }
                    _anyPushCompleted = true; // keys FinishAsync's drain-deadline choice
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Ordinary teardown, not a pump failure: PipelineHost
                // cancels the run CTS BEFORE disposing the streaming
                // session, and a session may check ct before its own
                // disposed guard (nemotron does) — so a canceled-ct push
                // can surface mid-drain. The `when` guard keeps OCEs with
                // an UNcancelled ct (e.g. a dispose-aborted cloud send)
                // classified exactly as before.
            }
            catch (Exception ex)
            {
                _pumpError = ex;
                log.LogWarning(ex, "streaming dictation pump failed");
                while (_frames.Reader.TryRead(out var leftover)) // unblock nothing-in-particular; drop leftovers
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -leftover.Length);
                    ArrayPool<float>.Shared.Return(leftover.Buffer);
                }
            }
        }, CancellationToken.None);
    }

    public static StreamingDictationSession Start(
        Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
        ILogger log,
        CancellationToken ct,
        TimeSpan? drainDeadline = null)
        => new(transcriberFactory, log, ct, drainDeadline);

    /// <summary>Called from the recorder's FramesAvailable event. Copies the
    /// frame into a POOLED buffer (defensive copy kept — the recorder contract
    /// allows buffer reuse — but without the per-frame float[] churn that
    /// feeds GC-pause suspicion: ~20 x 800-float allocations/s previously).
    /// Never blocks the capture thread.</summary>
    public void OnFrame(ReadOnlyMemory<float> frame)
    {
        var buffer = ArrayPool<float>.Shared.Rent(frame.Length);
        frame.Span.CopyTo(buffer);
        if (_frames.Writer.TryWrite(new PooledFrame(buffer, frame.Length)))
        {
            Interlocked.Increment(ref _queuedFrames);
            Interlocked.Add(ref _queuedSamples, frame.Length);
        }
        else
        {
            ArrayPool<float>.Shared.Return(buffer); // TryWrite false after completion — silent drop
        }
    }

    /// <summary>True after FinishAsync abandoned the session on drain timeout.
    /// Keys the null-return contract (FinishAsync returned null because the drain
    /// deadline expired, not because no transcriber materialized) and logging.
    /// The abandon path may have ORPHANED a pump still executing inside a native
    /// call on the shared ParakeetSession — callers coordinate any dispose of
    /// that shared resource via <see cref="PumpCompletion"/>.</summary>
    public bool DrainTimedOut { get; private set; }

    /// <summary>Per-finish metrics; non-null after FinishAsync returns
    /// (any outcome). See <see cref="StreamingFinishStats"/>.</summary>
    public StreamingFinishStats? FinishStats { get; private set; }

    /// <summary>Completes when the background pump exits (success, fault, or after an
    /// abandon finally unwedges). Callers that abandon this coordinator while this is
    /// incomplete must not dispose shared native resources (the ParakeetSession) until
    /// it completes.</summary>
    public Task PumpCompletion => _pump;

    /// <summary>Stop pumping and get the final transcript. Null when no transcriber
    /// materialized, or when the drain deadline expired (session abandoned +
    /// disposed — the caller's late batch path takes over). Rethrows an unrecovered
    /// pump failure — parity with today, where a batch TranscribeAsync exception
    /// also propagates to the pipeline.</summary>
    public async Task<TranscriptionResult?> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        // Backlog snapshot BEFORE completing the writer: frames queued but not
        // yet pumped. asr_wait below is the price of draining exactly this.
        var backlogFrames = Volatile.Read(ref _queuedFrames);
        var backlogMs = (int)(Interlocked.Read(ref _queuedSamples) / SamplesPerMs);
        _frames.Writer.TryComplete();
        // Short-circuit ONLY a session that actually started and still
        // completed zero pushes; a session still starting (cloud connect,
        // cold factory load) keeps the full deadline.
        var deadline = _anyPushCompleted || !_sessionStarted
            ? _drainDeadline
            : TimeSpan.FromTicks(Math.Min(_drainDeadline.Ticks, ZeroPushDrainDeadline.Ticks));
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _pump.WaitAsync(deadline, ct); // TimeoutException on a wedged drain
        }
        catch (TimeoutException)
        {
            // A wedged push HANGS rather than throws (half-dead socket send,
            // or a stuck synchronous native P/Invoke), so no exception-based
            // fallback inside the session/wrapper can fire. Bound the whole
            // post-stop wait HERE: abandon the session and return null so the
            // caller's late path transcribes fullAudio (bounded, batch). The
            // session dispose runs in the BACKGROUND: awaiting it inline can
            // block for as long as the wedged native call takes (observed
            // ~16 s in the wild — dispose cannot interrupt a P/Invoke, it just
            // queues behind the session's native gate). Callers that see
            // DrainTimedOut coordinate shared-native disposal via
            // PumpCompletion, exactly as before.
            // Stats note: never probe INativeCallStatsSource here — its
            // snapshot takes the native gate, which the wedged call is holding.
            waitSw.Stop();
            DrainTimedOut = true;
            FinishStats = new StreamingFinishStats(
                (int)waitSw.ElapsedMilliseconds, null, backlogFrames, backlogMs, null);
            _log.LogWarning(
                "streaming drain exceeded {DrainDeadline}; abandoning streaming session, batch path takes over",
                deadline);
            _ = ScheduleAbandonedSessionDispose();
            return null;
        }
        waitSw.Stop();
        var asrWaitMs = (int)waitSw.ElapsedMilliseconds;
        // Captured on the pump task; rethrow via ExceptionDispatchInfo so the
        // original stack trace survives this cross-thread rethrow.
        if (_pumpError is not null)
        {
            FinishStats = new StreamingFinishStats(asrWaitMs, null, backlogFrames, backlogMs, null);
            ExceptionDispatchInfo.Capture(_pumpError).Throw();
        }
        var session = _session;
        if (session is null)
        {
            FinishStats = new StreamingFinishStats(asrWaitMs, null, backlogFrames, backlogMs, null);
            return null;
        }
        var nativeSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await session.FinishAsync(fullAudio, ct);
        }
        finally
        {
            // Ordering: finish first, then dispose (FallbackStreamingTranscriber's
            // push-after-dispose guard documents why). The finally keeps the
            // session from leaking when FinishAsync throws — that exception
            // deliberately propagates to the pipeline (batch parity).
            nativeSw.Stop();
            FinishStats = new StreamingFinishStats(
                asrWaitMs,
                (int)nativeSw.ElapsedMilliseconds,
                backlogFrames,
                backlogMs,
                (session as INativeCallStatsSource)?.NativeCallStats);
            await DisposeSessionAsync();
        }
    }

    /// <summary>Dispose the abandoned session OFF every caller-facing await
    /// path. The immediate attempt aborts a socket-style session — which is
    /// what unwedges a pump stuck in a socket send. A native session's dispose
    /// cannot interrupt an in-flight P/Invoke: it only queues behind the
    /// session's native gate until the call returns, which is exactly why no
    /// caller may await it. After the pump exits, dispose again: that is the
    /// only point with a happens-before edge on the pump's late `_session`
    /// assignment (the pump may still have been inside StartSessionAsync when
    /// we abandoned).</summary>
    private Task ScheduleAbandonedSessionDispose()
        => Task.Run(async () =>
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch { /* pump error already logged */ }
            await DisposeSessionAsync().ConfigureAwait(false);
        });

    /// <summary>Abandon the dictation (silence-drop / cancel / drain timeout):
    /// stop the pump and dispose the session without transcribing. Never
    /// throws, and never blocks unboundedly: the session dispose runs in the
    /// background (for a socket session dispose aborts the socket and unwedges
    /// its pump; for a native session dispose cannot interrupt the in-flight
    /// P/Invoke and would otherwise block here behind the native gate), and
    /// the pump wait is bounded. Callers coordinate shared-native disposal via
    /// <see cref="PumpCompletion"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        // FinishAsync already proved the pump is wedged past the drain
        // deadline AND scheduled the background dispose chain — scheduling a
        // second chain or waiting on the pump again here only duplicates work
        // and delays the caller's late batch path.
        if (DrainTimedOut) return;
        var abandonDispose = ScheduleAbandonedSessionDispose();
        // Bounded: never let a pathologically hung pump (hanging factory, or
        // a wedged native call) block the serial hotkey loop; orphaning the
        // pump task is the lesser evil.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* abandoned */ }
        // Let the common (healthy) case observe a disposed session
        // synchronously; a chain blocked behind a wedged native call finishes
        // in the background.
        try { await abandonDispose.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* finishes in background */ }
    }

    private async ValueTask DisposeSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        try { await session.DisposeAsync(); } catch { /* abandoned */ }
    }
}
