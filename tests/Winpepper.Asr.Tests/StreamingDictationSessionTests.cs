using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingDictationSessionTests
{
    private sealed class RecordingStreamingTranscriber : IStreamingTranscriber
    {
        public string ModelName => "rec";
        public RecordingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class RecordingSession : IStreamingTranscriptionSession
        {
            public List<float[]> Pushed { get; } = new();
            public ReadOnlyMemory<float> FinishAudio { get; private set; }
            public bool Disposed { get; private set; }

            public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            { Pushed.Add(mono16k.ToArray()); return ValueTask.CompletedTask; }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
            { FinishAudio = fullAudio; return Task.FromResult(new TranscriptionResult("OK", "rec")); }

            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task FramesQueuedBeforeTheSessionIsReady_AreDeliveredInOrder()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var gate = new TaskCompletionSource<IStreamingTranscriber?>();
        var session = StreamingDictationSession.Start(
            _ => gate.Task, NullLogger.Instance, TestContext.Current.CancellationToken);

        session.OnFrame(new float[] { 1f });
        session.OnFrame(new float[] { 2f });
        gate.SetResult(transcriber); // transcriber becomes ready AFTER frames arrived
        session.OnFrame(new float[] { 3f });

        var result = await session.FinishAsync(new float[9], TestContext.Current.CancellationToken);

        result!.Text.ShouldBe("OK");
        transcriber.Session.Pushed.Select(f => f[0]).ShouldBe(new[] { 1f, 2f, 3f });
        transcriber.Session.FinishAudio.Length.ShouldBe(9);
        transcriber.Session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OnFrame_CopiesTheFrame_BeforeTheRecorderReusesItsBuffer()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        var buffer = new float[] { 42f };
        session.OnFrame(buffer);
        buffer[0] = -1f; // recorder reuses its buffer

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);
        transcriber.Session.Pushed[0][0].ShouldBe(42f);
    }

    [Fact]
    public async Task OnFrame_PooledCopy_PreservesExactLengthAndContent()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        var frame = new float[800];
        for (var i = 0; i < frame.Length; i++) frame[i] = i;
        session.OnFrame(frame);
        session.OnFrame(new float[] { 1f, 2f, 3f }); // pool rounds the rented array up

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);

        transcriber.Session.Pushed[0].Length.ShouldBe(800); // NOT the pool bucket size
        transcriber.Session.Pushed[0][799].ShouldBe(799f);
        transcriber.Session.Pushed[1].ShouldBe(new[] { 1f, 2f, 3f });
    }

    [Fact]
    public async Task NullFactory_FinishReturnsNull()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(null),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]); // dropped silently

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Dispose_AbandonsWithoutTranscribing_AndNeverThrows()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);

        await session.DisposeAsync();

        transcriber.Session.Disposed.ShouldBeTrue();
        transcriber.Session.FinishAudio.Length.ShouldBe(0); // FinishAsync never ran
    }

    [Fact]
    public async Task FinishThatThrows_StillDisposesTheSession()
    {
        // The session FinishAsync exception deliberately propagates to the
        // pipeline (batch parity) — but the inner session must not leak.
        var transcriber = new FakeStreamingTranscriber("cloud")
        {
            OnFinish = (_, _) => throw new InvalidOperationException("finish boom"),
        };
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[] { 1f });

        await Should.ThrowAsync<InvalidOperationException>(
            () => session.FinishAsync(new float[1], TestContext.Current.CancellationToken));

        transcriber.LastSession!.Disposed.ShouldBeTrue();
    }

    // Models the abandon race behind Bug A: dispose lands while the pump is
    // mid-push and MORE frames are already queued (completing the writer does
    // not stop ReadAllAsync from yielding them). DisposeAsync releases the
    // in-flight push SUCCESSFULLY — this is the ordinary silence-drop abandon,
    // not a failure. On the pre-fix code the pump's next iteration dereferences
    // the nulled `_session` field, NREs, and logs "streaming dictation pump
    // failed".
    private sealed class BlocksFirstPushTranscriber : IStreamingTranscriber
    {
        public string ModelName => "blocks-first-push";
        public BlockingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class BlockingSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _firstPushStarted = new();
            private readonly TaskCompletionSource _release = new();
            private int _pushes;

            public Task FirstPushStarted => _firstPushStarted.Task;
            public int PushCount => Volatile.Read(ref _pushes);

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                // Models nemotron: ct is checked BEFORE any disposed guard
                // (NemotronStreamingTranscriber.cs:84 vs :87), so a canceled-ct
                // push mid-drain throws OCE.
                ct.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref _pushes) == 1)
                {
                    _firstPushStarted.TrySetResult();
                    await _release.Task; // held until DisposeAsync abandons the dictation
                }
                // Pushes after dispose are the benign no-op both production
                // sessions implement — just count them.
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run — this dictation is abandoned");

            public ValueTask DisposeAsync()
            {
                _release.TrySetResult(); // the in-flight push completes normally
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task AbandonWithQueuedFrames_PumpDrainsWithoutError()
    {
        var log = new CapturingLogger();
        var transcriber = new BlocksFirstPushTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            log, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);
        session.OnFrame(new float[10]);
        session.OnFrame(new float[10]);
        await transcriber.Session.FirstPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await session.DisposeAsync(); // silence-drop abandon: frames 2 and 3 are still queued

        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The pump drained the remaining frames through its OWN reference —
        // no NRE, no "streaming dictation pump failed" noise.
        transcriber.Session.PushCount.ShouldBe(3);
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task TeardownCancel_MidDrain_IsBenign_NoPumpFailureWarning()
    {
        var log = new CapturingLogger();
        var transcriber = new BlocksFirstPushTranscriber();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            log, cts.Token);
        session.OnFrame(new float[10]);
        session.OnFrame(new float[10]);
        await transcriber.Session.FirstPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cts.Cancel();                 // PipelineHost teardown cancels _runCts FIRST (:1259)...
        await session.DisposeAsync(); // ...THEN disposes the streaming session (:1270)

        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The canceled-ct push mid-drain is ordinary teardown, not a pump
        // failure — no "streaming dictation pump failed" noise.
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task FactoryException_SurfacesAtFinish()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromException<IStreamingTranscriber?>(new InvalidOperationException("boom")),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);

        await Should.ThrowAsync<InvalidOperationException>(
            () => session.FinishAsync(new float[10], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FramesAfterFinish_AreDroppedSilently()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);
        session.OnFrame(new float[5]); // must not throw
    }

    private sealed class WedgedStreamingTranscriber : IStreamingTranscriber
    {
        public string ModelName => "wedged";
        public WedgedSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        // PushAsync HANGS instead of throwing (a half-dead socket send);
        // DisposeAsync aborts it, exactly like ClientWebSocket abort unblocks
        // a pending SendAsync.
        public sealed class WedgedSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            public bool Disposed { get; private set; }

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
                => await _wedge.Task;

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                _wedge.TrySetException(new ObjectDisposedException(nameof(WedgedSession)));
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task WedgedPush_DrainDeadlineExpires_ReturnsNullAndDisposesTheSession()
    {
        var transcriber = new WedgedStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull(); // caller's late batch path takes over (bounded)
        session.DrainTimedOut.ShouldBeTrue(); // keys the null-return contract
        // The dispose now runs in the BACKGROUND (it must never block
        // FinishAsync); for this socket-style fake it aborts the wedged push,
        // which is what lets the pump exit — so pump completion implies the
        // dispose ran.
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        transcriber.Session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task PumpCompletion_IsCompleted_AfterNormalFinish()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);

        await session.FinishAsync(new float[10], TestContext.Current.CancellationToken);

        // FinishAsync awaited the pump inside the drain deadline, so callers
        // see a completed pump and the orphan guard has nothing to track.
        session.PumpCompletion.IsCompleted.ShouldBeTrue();
    }

    // Like WedgedStreamingTranscriber, but DisposeAsync does NOT release the
    // wedge — modeling a pump stuck inside an uninterruptible native call that
    // no session abort can unblock. The test unwedges manually.
    private sealed class PermanentlyWedgedTranscriber : IStreamingTranscriber
    {
        public string ModelName => "permanently-wedged";
        public PermanentlyWedgedSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class PermanentlyWedgedSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            public bool Disposed { get; private set; }

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
                => await _wedge.Task;

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public ValueTask DisposeAsync()
            {
                Disposed = true; // dispose does NOT unwedge (native call in flight)
                return ValueTask.CompletedTask;
            }

            public void Unwedge() => _wedge.TrySetResult();
        }
    }

    [Fact]
    public async Task PumpCompletion_RemainsIncomplete_AfterDrainTimeoutAbandon()
    {
        var transcriber = new PermanentlyWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push, permanently

        // Returns at the drain deadline: the abandoned session's dispose is
        // scheduled in the background instead of being awaited inline
        // (nothing can unwedge this fake until the test does).
        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        // The pump is ORPHANED, still "inside" the wedged push. This is exactly
        // the state PipelineHost's orphan guard must observe via PumpCompletion:
        // no shared native dispose may run until it completes.
        session.PumpCompletion.IsCompleted.ShouldBeFalse();

        transcriber.Session.Unwedge();
        await session.PumpCompletion.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        session.PumpCompletion.IsCompleted.ShouldBeTrue();
    }

    // Models the NATIVE wedge from the 11:18:34 incident (Bug B): PushAsync
    // hangs inside what is really one synchronous P/Invoke, and DisposeAsync
    // BLOCKS behind the same per-session native gate until that call returns —
    // dispose cannot interrupt a P/Invoke; it can only queue behind the lock
    // (NemotronStreamingTranscriber.Session._nativeGate).
    private sealed class NativeGateWedgedTranscriber : IStreamingTranscriber
    {
        public string ModelName => "native-gate-wedged";
        public NativeGateWedgedSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class NativeGateWedgedSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            private readonly TaskCompletionSource _disposeDone = new();
            private readonly TaskCompletionSource _firstPushStarted = new();

            /// <summary>Completes when DisposeAsync actually finished (i.e. the
            /// wedged native call returned and the gate was released).</summary>
            public Task DisposeCompletion => _disposeDone.Task;

            /// <summary>Completes when the pump ENTERED the first (wedged) push.
            /// The pump sets its session-started flag BEFORE the push loop, so
            /// awaiting this guarantees the coordinator observed a started
            /// session — without it FinishAsync can race the pump's Task.Run
            /// startup and classify the session as still starting.</summary>
            public Task FirstPushStarted => _firstPushStarted.Task;

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                _firstPushStarted.TrySetResult();
                await _wedge.Task; // the wedged native feed
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public async ValueTask DisposeAsync()
            {
                await _wedge.Task; // lock(_nativeGate): dispose queues behind the in-flight call
                _disposeDone.TrySetResult();
            }

            public void Unwedge() => _wedge.TrySetResult();
        }
    }

    [Fact]
    public async Task WedgedNativePush_FinishReturnsPromptly_DisposeIsDeferredBehindThePump()
    {
        var transcriber = new NativeGateWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push

        // Bounded by the drain deadline + a small epsilon, NOT by the blocked
        // dispose: on the pre-fix code this call NEVER returns (FinishAsync
        // inline-awaited a DisposeAsync that is itself stuck behind the wedged
        // native call), so the 3 s guard below trips.
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        session.PumpCompletion.IsCompleted.ShouldBeFalse();           // pump orphaned inside the native call
        transcriber.Session.DisposeCompletion.IsCompleted.ShouldBeFalse(); // dispose queued behind that call

        transcriber.Session.Unwedge(); // the native call finally returns
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // ...and only now can the (background) dispose complete.
        await transcriber.Session.DisposeCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline()
    {
        var transcriber = new NativeGateWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(30)); // the FULL deadline: must NOT be waited out
        session.OnFrame(new float[800]); // the pump wedges on the FIRST push — zero pushes ever complete
        // The shortcut applies only to a STARTED session, so the coordinator
        // must observe _sessionStarted before FinishAsync reads it. Without
        // this wait FinishAsync races the pump's Task.Run startup: on a
        // loaded machine the pump hasn't run yet, the session classifies as
        // still starting, and the FULL 30 s deadline (correctly) applies.
        await transcriber.Session.FirstPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Zero completed pushes at stop time means there is no streamed-latency
        // win to preserve — the short deadline applies, not the 30 s one. The
        // 10 s guard sits between the two so the wrong branch fails loudly.
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();

        transcriber.Session.Unwedge(); // let the orphaned pump exit cleanly
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // First push completes (streaming genuinely underway), the SECOND wedges —
    // the zero-push shortcut must NOT apply and the full deadline must hold.
    private sealed class WedgesOnSecondPushTranscriber : IStreamingTranscriber
    {
        public string ModelName => "wedges-on-second-push";
        public WedgingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class WedgingSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            private readonly TaskCompletionSource _secondPushStarted = new();
            private int _pushes;

            /// <summary>The second push starting proves the first COMPLETED —
            /// and therefore that the coordinator observed a completed push.</summary>
            public Task SecondPushStarted => _secondPushStarted.Task;

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _pushes) == 1) return; // first push succeeds
                _secondPushStarted.TrySetResult();
                await _wedge.Task;
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public void Unwedge() => _wedge.TrySetResult();
        }
    }

    [Fact]
    public async Task StreamingUnderway_AtFinish_KeepsTheFullDrainDeadline()
    {
        var transcriber = new WedgesOnSecondPushTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(3)); // full deadline, above the 1.5 s short one
        session.OnFrame(new float[800]); // completes
        session.OnFrame(new float[800]); // wedges
        await transcriber.Session.SecondPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var finishSw = Stopwatch.StartNew();
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        finishSw.Stop();

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        // The FULL 3 s deadline applied — not the 1.5 s zero-push shortcut
        // (0.5 s margin absorbs timer slop).
        finishSw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5));

        transcriber.Session.Unwedge();
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // A healthy session still STARTING at stop time must keep the FULL drain
    // deadline: cloud connect is designed-bounded at 10 s and a cold factory
    // load takes seconds — both sit in FRONT of the first push. The
    // zero-push shortcut exists to cut pointless waiting on a session that
    // started and then wedged, not to abandon one never given a chance to
    // start (which would systematically convert healthy dictations to local
    // batch).
    private sealed class NeverStartsTranscriber : IStreamingTranscriber
    {
        private readonly TaskCompletionSource<IStreamingTranscriptionSession> _tcs = new();
        public string ModelName => "never-starts";
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct) => _tcs.Task;
        public void Release() => _tcs.TrySetCanceled(); // let the orphaned pump exit
    }

    [Fact]
    public async Task SessionStillStarting_AtFinish_KeepsTheFullDrainDeadline()
    {
        var transcriber = new NeverStartsTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(3)); // full deadline, above the 1.5 s short one
        session.OnFrame(new float[800]); // queued; the pump is still inside StartSessionAsync

        var finishSw = Stopwatch.StartNew();
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        finishSw.Stop();

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        // The FULL 3 s deadline applied — a still-starting session must not be
        // short-circuited by the zero-push shortcut (0.5 s margin absorbs slop).
        finishSw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5));

        transcriber.Release();
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FinishAsync_ReportsBacklogAndSpans_InFinishStats()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var gate = new TaskCompletionSource<IStreamingTranscriber?>();
        var session = StreamingDictationSession.Start(
            _ => gate.Task, NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);
        session.OnFrame(new float[800]);
        session.OnFrame(new float[800]);

        // FinishAsync runs synchronously up to its first await, capturing the
        // backlog BEFORE the transcriber (and thus the pump's pushes) exists.
        var finish = session.FinishAsync(new float[9], TestContext.Current.CancellationToken);
        gate.SetResult(transcriber);

        (await finish).ShouldNotBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.BacklogFrames.ShouldBe(3);
        stats.BacklogMs.ShouldBe(150); // 2400 samples / 16 per ms
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(0);
        stats.AsrNativeMs.ShouldNotBeNull();
    }

    [Fact]
    public async Task FinishAsync_DrainTimeout_StillReportsWaitAndBacklog()
    {
        var transcriber = new PermanentlyWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push
        session.OnFrame(new float[800]); // stays queued behind the wedge

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(150); // paid the ~200 ms deadline
        stats.AsrNativeMs.ShouldBeNull();                  // inner finish never ran
        stats.NativeCallStats.ShouldBeNull();              // never probed on abandon (gate may be wedged)
        // Frame 1 may or may not have been dequeued by the pump before
        // FinishAsync captured the backlog — both are legitimate.
        stats.BacklogFrames.ShouldBeInRange(1, 2);

        transcriber.Session.Unwedge();
        await session.PumpCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FinishAsync_SurfacesNativeCallStats_WhenSessionExposesThem()
    {
        var transcriber = new StatsExposingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).ShouldNotBeNull();

        session.FinishStats.ShouldNotBeNull()
            .NativeCallStats.ShouldBe(new NativeCallStats(7, 900, 400, 2));
    }

    [Fact]
    public async Task FinishAsync_PropagatesOver250Ticks_ThroughFinishStats()
    {
        var transcriber = new StatsExposingTranscriber();
        transcriber.Session.NativeCallStats = new NativeCallStats(7, 900, 400, 2,
            Over250StartTicks: new long[] { 100_000, 100_400 }, Over250Overflow: 3);
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).ShouldNotBeNull();

        var ns = session.FinishStats.ShouldNotBeNull().NativeCallStats.ShouldNotBeNull();
        ns.Over250StartTicks.ShouldBe(new long[] { 100_000, 100_400 });
        ns.Over250Overflow.ShouldBe(3);
    }

    [Fact]
    public async Task FinishAsync_NullFactory_StillSetsFinishStats()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(null),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrNativeMs.ShouldBeNull(); // no session ever materialized
    }

    private sealed class StatsExposingTranscriber : IStreamingTranscriber
    {
        public string ModelName => "stats";
        public StatsSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class StatsSession : IStreamingTranscriptionSession, INativeCallStatsSource
        {
            public NativeCallStats NativeCallStats { get; set; } = new(7, 900, 400, 2);
            public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct) => ValueTask.CompletedTask;
            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => Task.FromResult(new TranscriptionResult("OK", "stats"));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
