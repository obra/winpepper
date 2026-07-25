using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Per-dictation glue between the audio frame event and a streaming session.
/// Frames are copied into an unbounded channel on the capture thread (never
/// blocking it) and pumped into the session on a background task. The session
/// may not exist yet when the first frames arrive — the transcriber factory
/// (model ensure + build) runs concurrently on the pump — so frames queue until
/// it is ready. FinishAsync completes the pump and returns the final transcript,
/// or null when no transcriber materialized (caller uses the batch-adapter path).
/// The pump drain is bounded by a drain deadline (default 10 s): a wedged push
/// (half-dead socket) HANGS rather than throws, so on timeout FinishAsync
/// abandons + disposes the session and returns null (A10 — the whole post-stop
/// wait stays bounded; the caller's batch path takes over).
/// </summary>
public sealed class StreamingDictationSession : IAsyncDisposable
{
    private readonly Channel<float[]> _frames = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private readonly ILogger _log;
    private readonly TimeSpan _drainDeadline;
    private IStreamingTranscriptionSession? _session;
    private Exception? _pumpError;

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
                    await foreach (var _ in _frames.Reader.ReadAllAsync(CancellationToken.None)) { }
                    return;
                }
                _session = await transcriber.StartSessionAsync(ct);
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    await _session.PushAsync(frame, ct);
            }
            catch (Exception ex)
            {
                _pumpError = ex;
                log.LogWarning(ex, "streaming dictation pump failed");
                while (_frames.Reader.TryRead(out _)) { } // unblock nothing-in-particular; drop leftovers
            }
        }, CancellationToken.None);
    }

    public static StreamingDictationSession Start(
        Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
        ILogger log,
        CancellationToken ct,
        TimeSpan? drainDeadline = null)
        => new(transcriberFactory, log, ct, drainDeadline);

    /// <summary>Called from the recorder's FramesAvailable event. Copies the frame
    /// (the recorder may reuse its buffer) and never blocks the capture thread.</summary>
    public void OnFrame(ReadOnlyMemory<float> frame)
        => _frames.Writer.TryWrite(frame.ToArray()); // TryWrite is false after completion — silent drop

    /// <summary>True after FinishAsync abandoned the session on drain timeout.
    /// The caller's late path keys its ensure-skip on this: the abandon path may
    /// have ORPHANED a pump still executing inside a native call on the shared
    /// ParakeetSession, so a model ensure (whose swap disposes that session)
    /// must not run.</summary>
    public bool DrainTimedOut { get; private set; }

    /// <summary>Stop pumping and get the final transcript. Null when no transcriber
    /// materialized, or when the drain deadline expired (session abandoned +
    /// disposed — the caller's late batch path takes over). Rethrows an unrecovered
    /// pump failure — parity with today, where a batch TranscribeAsync exception
    /// also propagates to the pipeline.</summary>
    public async Task<TranscriptionResult?> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        _frames.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(_drainDeadline, ct); // TimeoutException on a wedged drain
        }
        catch (TimeoutException)
        {
            // A wedged push (half-dead socket) HANGS rather than throws, so no
            // exception-based fallback inside the session/wrapper can fire and
            // the cloud deadline (scheduled inside the wrapper's FinishAsync)
            // never starts. Bound the whole post-stop wait HERE: abandon the
            // session — disposing it aborts the socket, which is what unblocks
            // the pump — and return null so the caller's late path transcribes
            // fullAudio (bounded, batch).
            DrainTimedOut = true; // late path must NOT ensure (orphaned-pump risk)
            _log.LogWarning(
                "streaming drain exceeded {DrainDeadline}; abandoning streaming session, batch path takes over",
                _drainDeadline);
            await DisposeAsync();
            return null;
        }
        // Captured on the pump task; rethrow via ExceptionDispatchInfo so the
        // original stack trace survives this cross-thread rethrow.
        if (_pumpError is not null) ExceptionDispatchInfo.Capture(_pumpError).Throw();
        if (_session is null) return null;
        var result = await _session.FinishAsync(fullAudio, ct);
        await _session.DisposeAsync();
        _session = null;
        return result;
    }

    /// <summary>Abandon the dictation (silence-drop / cancel / drain timeout): stop
    /// the pump and dispose the session without transcribing. Never throws. Disposes
    /// the session BEFORE awaiting the pump — a wedged push never completes on its
    /// own; aborting the session is what unblocks it — then re-disposes after the
    /// pump exits, covering the pump-assigned-the-session-late race.</summary>
    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        await DisposeSessionAsync();
        // Bounded: never let a pathologically hung pump (e.g. a hanging factory)
        // block the serial hotkey loop; orphaning the pump task is the lesser evil.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* abandoned */ }
        await DisposeSessionAsync();
    }

    private async ValueTask DisposeSessionAsync()
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        try { await session.DisposeAsync(); } catch { /* abandoned */ }
    }
}
