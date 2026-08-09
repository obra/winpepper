using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerProcessEngineTests
{
    private static readonly WorkerEngineOptions FastTimeouts = new()
    {
        LoadTimeout = TimeSpan.FromSeconds(5),
        BeginStreamTimeout = TimeSpan.FromSeconds(5),
        FeedTimeout = TimeSpan.FromMilliseconds(300),
        FinalizeTimeout = TimeSpan.FromSeconds(5),
        BatchTimeout = TimeSpan.FromSeconds(5),
        DisposeTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static WorkerProcessEngine Engine(InProcessWorkerChannelFactory factory,
        WorkerRestartPolicy? policy = null)
        => new(factory, "/runtime", "/model.gguf", "nemotron-streaming-en", FastTimeouts, policy);

    [Fact]
    public void StreamRoundTrip_ThroughRealLoop_ReturnsFinalTextAndGateWait()
    {
        var fake = new FakeTranscribeCppEngine { FinalText = "worker says hi", GateWaitMsToReport = 3 };
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);

        using var stream = engine.BeginStream(13, "en-US", out var gateWaitMs);
        gateWaitMs.ShouldBe(3);
        stream.Feed(new float[2560], 2560);
        var (text, truncated) = stream.Finalize();
        text.ShouldBe("worker says hi");
        truncated.ShouldBeFalse();
        factory.Started.ShouldBe(1);
    }

    [Fact]
    public void Batch_RoundTrip_ReturnsText()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        var text = engine.TranscribeBatch(new float[16], null, out _);
        text.ShouldNotBeNull();
    }

    [Fact]
    public void WorkerException_SurfacesAsTranscribeCppException()
    {
        var fake = new FakeTranscribeCppEngine { ThrowOnBeginStream = true };
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        Should.Throw<TranscribeCppException>(() => engine.BeginStream(13, null, out _));
    }

    [Fact]
    public void WedgedFeed_TimesOut_KillsWorker_AndNextCallRespawns()
    {
        using var feedGate = new ManualResetEventSlim(false); // deterministic wedge
        var first = true;
        var factory = new InProcessWorkerChannelFactory(() =>
        {
            if (first) { first = false; return new FakeTranscribeCppEngine { FeedGate = feedGate }; }
            return new FakeTranscribeCppEngine(); // the respawned worker is healthy
        });

        using var engine = Engine(factory);
        using var stream = engine.BeginStream(13, null, out _);

        Should.Throw<TranscribeCppException>(() => stream.Feed(new float[2560], 2560)); // times out at 300 ms
        factory.Last!.HasExited.ShouldBeTrue(); // the wedged worker was killed

        // Next engine call transparently respawns a fresh worker:
        var text = engine.TranscribeBatch(new float[16], null, out _);
        text.ShouldNotBeNull();
        factory.Started.ShouldBe(2);
        feedGate.Set(); // release the wedged background thread
    }

    [Fact]
    public void StreamProxy_AfterWorkerDeath_ThrowsOnUse_AndDisposeIsBenign()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        var stream = engine.BeginStream(13, null, out _);
        factory.Last!.Kill();

        Should.Throw<TranscribeCppException>(() => stream.Finalize());
        Should.NotThrow(() => stream.Dispose());
    }

    [Fact]
    public void RestartBudgetExhausted_ThrowsWithoutSpawning()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        var factory = new InProcessWorkerChannelFactory(FailingEngine);
        using var engine = Engine(factory, policy);

        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        var spawnsAfterFirstFailure = factory.Started;

        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        factory.Started.ShouldBe(spawnsAfterFirstFailure); // budget blocked the respawn

        now = 60_000;
        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        factory.Started.ShouldBe(spawnsAfterFirstFailure + 1); // cooldown elapsed -> one retry

        static ITranscribeCppEngine FailingEngine() => throw new TranscribeCppException("model load failed");
    }

    [Fact]
    public void Dispose_ThenCall_ThrowsObjectDisposed_AndDoesNotRespawn()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        var engine = Engine(factory);
        engine.TranscribeBatch(new float[16], null, out _); // spawn + load once (also settles the reader before Kill)
        var startedBeforeDispose = factory.Started;

        engine.Dispose();

        Should.Throw<ObjectDisposedException>(() => engine.TranscribeBatch(new float[16], null, out _));
        Should.Throw<ObjectDisposedException>(() => engine.BeginStream(13, null, out _));
        factory.Started.ShouldBe(startedBeforeDispose); // a disposed engine NEVER respawns a worker
    }

    [Fact]
    public void Batch_OversizeAudio_ThrowsInvalidOperation_WithoutTouchingTheWorker()
    {
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory);
        // Just over the 64 MiB frame cap (~17 min at 16 kHz). One ~67 MB array
        // in a unit test is wasteful but acceptable.
        var oversize = new float[WorkerWire.MaxPayloadBytes / sizeof(float) + 1];
        var ex = Should.Throw<InvalidOperationException>(() => engine.TranscribeBatch(oversize, null, out _));
        ex.Message.ShouldContain("dictation too long");
        factory.Started.ShouldBe(0); // the pre-check fired before any spawn/RPC
    }

    [Fact]
    public void OperationWedge_ThreeConsecutiveKills_ExhaustRestartBudget_ThenCooldownAllowsOneRetry()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 3, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        using var feedGate = new ManualResetEventSlim(false); // never set until the end: EVERY feed wedges
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine { FeedGate = feedGate });
        using var engine = Engine(factory, policy);

        for (var i = 1; i <= 3; i++)
        {
            using var stream = engine.BeginStream(13, null, out _); // respawn allowed while budget remains
            Should.Throw<TranscribeCppException>(() => stream.Feed(new float[2560], 2560)); // wedge -> 300 ms timeout -> kill
            factory.Last!.HasExited.ShouldBeTrue();
        }
        factory.Started.ShouldBe(3);

        // Wedge x3 exhausted the budget: the next call must NOT spawn a 4th worker.
        var blocked = Should.Throw<TranscribeCppException>(() => engine.BeginStream(13, null, out _));
        blocked.Message.ShouldContain("restart budget exhausted");
        factory.Started.ShouldBe(3);

        // Cooldown engages: exactly one attempt is allowed after 60 s.
        now = 60_000;
        Should.NotThrow(() => engine.BeginStream(13, null, out _));
        factory.Started.ShouldBe(4);

        feedGate.Set(); // release the parked worker threads
    }

    /// <summary>The headline scenario the subprocess exists for: a wedged
    /// native feed no longer wedges the app — the streaming transcriber falls
    /// back to batch on a FRESH worker and the dictation still yields text.</summary>
    [Fact]
    public async Task EndToEnd_WedgedStream_FallsBackToNemotronBatch_OnFreshWorker()
    {
        using var feedGate = new ManualResetEventSlim(false);
        var first = true;
        var factory = new InProcessWorkerChannelFactory(() =>
        {
            if (first) { first = false; return new FakeTranscribeCppEngine { FeedGate = feedGate }; }
            return new FakeTranscribeCppEngine(); // the respawned worker is healthy
        });
        using var engine = Engine(factory);
        var batch = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-en-batch");
        var streaming = new NemotronStreamingTranscriber(() => engine, batch, "nemotron-streaming-en",
            NullLogger<NemotronStreamingTranscriber>.Instance);

        await using var session = await streaming.StartSessionAsync(CancellationToken.None);
        await session.PushAsync(new float[2560], CancellationToken.None); // wedges -> times out -> corrupt
        var result = await session.FinishAsync(new float[2560], CancellationToken.None);

        result.ProviderModelName.ShouldBe("nemotron-streaming-en-batch"); // batch fallback produced it
        factory.Started.ShouldBe(2); // wedged worker killed, fresh worker served the batch
        feedGate.Set();
    }

    [Fact]
    public void NaturalDeathBetweenRpcs_ChargesTheRestartBudget()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory, policy);

        engine.TranscribeBatch(new float[16], null, out _); // healthy spawn; completed batch resets the budget
        factory.All[0].SimulateNaturalExit();               // dies between RPCs

        // The death is charged BEFORE the respawn attempt, so with a
        // 1-failure budget no second worker may spawn.
        var ex = Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[16], null, out _));
        ex.Message.ShouldContain("restart budget exhausted");
        factory.Started.ShouldBe(1);

        now = 60_000; // cooldown elapsed -> one respawn attempt allowed
        Should.NotThrow(() => engine.TranscribeBatch(new float[16], null, out _));
        factory.Started.ShouldBe(2);
    }

    [Fact]
    public void NaturalExitRespawn_InvalidatesStaleStreamProxy_AndDisposesTheOldProcess()
    {
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory);

        var staleStream = engine.BeginStream(13, null, out _); // dictation 1 opens a stream
        factory.All[0].SimulateNaturalExit();                   // its worker dies on its own

        engine.TranscribeBatch(new float[16], null, out _);     // dictation 2 triggers the respawn
        factory.Started.ShouldBe(2);
        factory.All[0].DisposeCalls.ShouldBe(1); // old process retired through KillLocked, not leaked

        // The stale proxy must see "stream lost", NOT reach dictation 2's
        // fresh one-stream worker (pre-fix it RPCs the fresh worker and gets
        // its "no open stream (send BeginStream first)" error instead).
        var ex = Should.Throw<TranscribeCppException>(() => staleStream.Feed(new float[2560], 2560));
        ex.Message.ShouldContain("stream lost");
        Should.NotThrow(() => staleStream.Dispose()); // benign no-op on a lost stream
    }
}
