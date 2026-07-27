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
        transcriber.Session.Disposed.ShouldBeTrue();
        session.DrainTimedOut.ShouldBeTrue(); // keys the null-return contract
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

        // Takes ~5 s: the internal DisposeAsync also bounds its pump wait before
        // abandoning (nothing can unwedge this fake until the test does).
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
}
