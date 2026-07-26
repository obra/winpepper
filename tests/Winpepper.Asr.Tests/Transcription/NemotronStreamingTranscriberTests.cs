using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class NemotronStreamingTranscriberTests
{
    private static float[] Samples(int n) => new float[n];

    private static NemotronStreamingTranscriber Make(
        FakeTranscribeCppEngine engine, FakeTranscriber batch)
        => new(() => engine, batch, "nemotron-streaming-en", NullLogger.Instance);

    [Fact]
    public async Task Streams_in_160ms_chunks_and_returns_final_text()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);

        // 8000-sample pre-roll (production's 500 ms) => 3 full 2560 feeds, 320 buffered
        await s.PushAsync(Samples(8000), CancellationToken.None);
        Assert.Equal(new[] { 2560, 2560, 2560 }, engine.LastStream!.FeedCounts);

        // 800-sample frames accumulate: 320 + 800*3 = 2720 => one more feed at the 3rd frame
        await s.PushAsync(Samples(800), CancellationToken.None);
        await s.PushAsync(Samples(800), CancellationToken.None);
        Assert.Equal(3, engine.LastStream.FeedCounts.Count);
        await s.PushAsync(Samples(800), CancellationToken.None);
        Assert.Equal(4, engine.LastStream.FeedCounts.Count);

        var result = await s.FinishAsync(Samples(10400), CancellationToken.None);
        Assert.True(engine.LastStream.Finalized);
        Assert.Equal("hello world final", result.Text);
        Assert.Equal("nemotron-streaming-en", result.ProviderModelName);
    }

    [Fact]
    public async Task Remainder_is_flushed_before_finalize()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, FakeTranscriber.Returning("batch", "batch text"));
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(3000), CancellationToken.None);         // one 2560 feed, 440 left
        await s.FinishAsync(Samples(3000), CancellationToken.None);
        Assert.Equal(new[] { 2560, 440 }, engine.LastStream!.FeedCounts); // tail flushed
    }

    [Fact]
    public async Task Zero_pushed_audio_goes_straight_to_batch_without_a_stream()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        var result = await s.FinishAsync(Samples(16000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
        Assert.True(engine.LastStream is null || !engine.LastStream.Finalized);
    }

    [Fact]
    public async Task Empty_final_text_falls_back_to_batch()   // blank-collapse-era guard
    {
        var engine = new FakeTranscribeCppEngine { FinalText = "   " };
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Truncated_stream_falls_back_to_batch()
    {
        var engine = new FakeTranscribeCppEngine { FinalWasTruncated = true };
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Feed_failure_never_throws_from_Push_and_finishes_via_batch()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnFeed = true };
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);   // must NOT throw
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Finalize_failure_falls_back_to_batch()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnFinalize = true };
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Engine_provider_failure_still_yields_a_batch_result()
    {
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = new NemotronStreamingTranscriber(
            () => throw new TranscribeCppException("engine unavailable"),
            batch, "nemotron-streaming-en", NullLogger.Instance);
        await using var s = await t.StartSessionAsync(CancellationToken.None);  // must NOT throw
        await s.PushAsync(Samples(4000), CancellationToken.None);
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
    }

    [Fact]
    public async Task Stream_is_disposed_but_engine_is_not()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, FakeTranscriber.Returning("batch", "batch text"));
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(4000), CancellationToken.None);
        await s.FinishAsync(Samples(4000), CancellationToken.None);
        await s.DisposeAsync();
        Assert.True(engine.LastStream!.Disposed);
        Assert.False(engine.Disposed);
    }

    [Fact]
    public async Task Uses_default_att_context_right_13()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, FakeTranscriber.Returning("batch", "batch text"));
        await using var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        Assert.Equal(13, engine.LastStream!.AttContextRight);
    }

    // Dispose-is-abort contract: the pipeline disposes sessions while pushes
    // may still arrive (cancel / silence-drop / drain-timeout / teardown).
    [Fact]
    public async Task Push_after_dispose_is_a_harmless_no_op()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = Make(engine, FakeTranscriber.Returning("batch", "batch text"));
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        await s.DisposeAsync();                                   // pipeline abort path
        await s.PushAsync(Samples(2560), CancellationToken.None); // must NOT throw
        Assert.Single(engine.LastStream!.FeedCounts);             // no native touch after dispose
    }

    [Fact]
    public async Task Finish_after_dispose_falls_back_to_batch_without_native_calls()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = FakeTranscriber.Returning("batch", "batch text");
        var t = Make(engine, batch);
        var s = await t.StartSessionAsync(CancellationToken.None);
        await s.PushAsync(Samples(2560), CancellationToken.None);
        await s.DisposeAsync();
        var result = await s.FinishAsync(Samples(4000), CancellationToken.None);
        Assert.Equal("batch text", result.Text);
        Assert.False(engine.LastStream!.Finalized);
    }
}
