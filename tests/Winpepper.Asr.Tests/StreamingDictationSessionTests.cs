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
