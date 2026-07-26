using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class AssemblyAiStreamingTests
{
    private sealed class StubKeyStore : IAssemblyAiKeyStore
    {
        private readonly string? _key;
        public StubKeyStore(string? key) => _key = key;
        public bool HasKey => _key is not null;
        public void Save(string apiKey) { }
        public string? Load() => _key;
        public void Clear() { }
    }

    private static AssemblyAiStreamingTranscriber NewTranscriber(
        FakeStreamingWebSocket socket, string? key = "k-123", ITranscriber? batchFallback = null)
        => new(() => socket,
            batchFallback ?? FakeTranscriber.Returning("assemblyai/slam-1", "BATCH-REST"),
            new StubKeyStore(key), new AssemblyAiOptions(),
            NullLogger<AssemblyAiStreamingTranscriber>.Instance);

    [Fact]
    public async Task Start_ConnectsWithKeyAndStreamingUri()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        socket.ApiKey.ShouldBe("k-123");
        socket.ConnectedUri!.ToString().ShouldBe(
            "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true");
    }

    [Fact]
    public async Task Start_WithoutKey_ThrowsAuthError()
    {
        var ex = await Should.ThrowAsync<AssemblyAiException>(
            () => NewTranscriber(new FakeStreamingWebSocket(), key: null)
                .StartSessionAsync(TestContext.Current.CancellationToken));
        ex.IsAuthError.ShouldBeTrue();
    }

    [Fact]
    public async Task Start_WhenConnectFails_DisposesTheSocket()
    {
        var socket = new FakeStreamingWebSocket { ThrowOnConnect = new InvalidOperationException("connect refused") };

        await Should.ThrowAsync<InvalidOperationException>(
            () => NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken));

        socket.Disposed.ShouldBeTrue(); // no leak: the underlying socket is torn down per failed connect
    }

    [Fact]
    public async Task Push_SplitsAnOversizedBufferIntoAtMost1000MsMessages()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        await session.PushAsync(new float[40_000], TestContext.Current.CancellationToken); // 2.5 s in one push

        socket.BinaryFrames.Count.ShouldBe(3); // 1000 ms + 1000 ms + 500 ms — never above the API max
        socket.BinaryFrames[0].Length.ShouldBe(32_000); // 16000 samples (1000 ms) * 2 bytes
        socket.BinaryFrames[1].Length.ShouldBe(32_000);
        socket.BinaryFrames[2].Length.ShouldBe(16_000); // 8000-sample remainder (>= 50 ms, so it sends)
    }

    [Fact]
    public async Task Push_KeepsASubMinimumResidualBufferedAfterAMaxSizeSend()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        await session.PushAsync(new float[16_400], TestContext.Current.CancellationToken); // 1000 ms + 25 ms
        socket.BinaryFrames.Count.ShouldBe(1); // the 25 ms residual stays buffered (under the API minimum)
        socket.BinaryFrames[0].Length.ShouldBe(32_000);

        await session.PushAsync(new float[400], TestContext.Current.CancellationToken); // residual reaches 50 ms
        socket.BinaryFrames.Count.ShouldBe(2);
        socket.BinaryFrames[1].Length.ShouldBe(1_600); // 800 samples * 2 bytes
    }

    [Fact]
    public async Task Push_CoalescesToAtLeast50MsBinaryMessages()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        await session.PushAsync(new float[400], TestContext.Current.CancellationToken); // 25 ms — buffered
        socket.BinaryFrames.ShouldBeEmpty();
        await session.PushAsync(new float[400], TestContext.Current.CancellationToken); // now 50 ms
        socket.BinaryFrames.Count.ShouldBe(1);
        socket.BinaryFrames[0].Length.ShouldBe(1600); // 800 samples * 2 bytes
    }

    [Fact]
    public async Task Finish_SendsTerminate_AndAssemblesTurnsInOrder()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);

        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":0,\"end_of_turn\":true,\"turn_is_formatted\":false,\"transcript\":\"hello world\"}");
        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":0,\"end_of_turn\":true,\"turn_is_formatted\":true,\"transcript\":\"Hello, world.\"}");
        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":1,\"end_of_turn\":true,\"turn_is_formatted\":true,\"transcript\":\"Second turn.\"}");

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        socket.TextFrames.ShouldContain(t => t.Contains("\"Terminate\""));
        result.Text.ShouldBe("Hello, world. Second turn."); // formatted replaces unformatted; ordered by turn_order
        result.ProviderModelName.ShouldBe("assemblyai/universal-streaming");
    }

    [Fact]
    public async Task Finish_WithZeroPushedAudio_DelegatesToTheBatchFallback()
    {
        // A9: bursting the buffer over the socket is throttled to ~1.25x realtime
        // server-side (and can be killed with error 3007), so the zero-pushed
        // path must delegate to the cloud batch REST transcriber instead.
        ReadOnlyMemory<float> seen = default;
        var fallback = new CapturingBatchTranscriber(m => seen = m);
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket, batchFallback: fallback)
            .StartSessionAsync(TestContext.Current.CancellationToken);

        var result = await session.FinishAsync(new float[40000], TestContext.Current.CancellationToken); // 2.5 s

        result.Text.ShouldBe("BATCH-REST");
        seen.Length.ShouldBe(40000);            // the fallback got the whole buffer
        fallback.Calls.ShouldBe(1);
        socket.BinaryFrames.ShouldBeEmpty();    // nothing was burst over the socket
    }

    [Fact]
    public async Task UnexpectedSocketClose_WithoutTermination_SurfacesAsErrorAtFinish()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);

        socket.CloseFromServer(); // closes WITHOUT a prior Termination or Error

        // Give the receive loop a beat to consume the close, then finish.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<AssemblyAiException>(
            () => session.FinishAsync(new float[800], TestContext.Current.CancellationToken));
    }

    /// <summary>Records the buffer handed to the batch fallback.</summary>
    private sealed class CapturingBatchTranscriber : ITranscriber
    {
        private readonly Action<ReadOnlyMemory<float>> _capture;
        public int Calls { get; private set; }
        public CapturingBatchTranscriber(Action<ReadOnlyMemory<float>> capture) => _capture = capture;
        public string ModelName => "assemblyai/slam-1";
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { Calls++; _capture(mono16k); return Task.FromResult(new TranscriptionResult("BATCH-REST", ModelName)); }
    }

    [Fact]
    public async Task ServerError_SurfacesAsAssemblyAiExceptionAtFinish()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        socket.EnqueueServerMessage("{\"type\":\"Error\",\"error\":\"bad audio\"}");

        // Give the receive loop a beat to consume the error, then finish.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<AssemblyAiException>(
            () => session.FinishAsync(new float[800], TestContext.Current.CancellationToken));
    }
}
