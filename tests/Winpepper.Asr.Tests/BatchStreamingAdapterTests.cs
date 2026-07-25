using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class BatchStreamingAdapterTests
{
    [Fact]
    public void ModelName_PassesThrough()
    {
        var adapter = new BatchStreamingAdapter(FakeTranscriber.Returning("m1", "hi"));
        adapter.ModelName.ShouldBe("m1");
    }

    [Fact]
    public async Task Finish_TranscribesTheFullBuffer_IgnoringPushedFrames()
    {
        ReadOnlyMemory<float> seen = default;
        var inner = new FakeTranscriber("m1", () => Task.FromResult(new TranscriptionResult("hello", "m1")));
        var adapter = new BatchStreamingAdapter(new CapturingTranscriber(inner, m => seen = m));

        await using var session = await adapter.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[123], TestContext.Current.CancellationToken); // ignored
        var full = new float[456];
        var result = await session.FinishAsync(full, TestContext.Current.CancellationToken);

        result.Text.ShouldBe("hello");
        seen.Length.ShouldBe(456); // FinishAsync's fullAudio is authoritative
        inner.Calls.ShouldBe(1);
    }

    /// <summary>Records the buffer handed to the wrapped transcriber.</summary>
    private sealed class CapturingTranscriber : ITranscriber
    {
        private readonly ITranscriber _inner;
        private readonly Action<ReadOnlyMemory<float>> _capture;
        public CapturingTranscriber(ITranscriber inner, Action<ReadOnlyMemory<float>> capture)
        { _inner = inner; _capture = capture; }
        public string ModelName => _inner.ModelName;
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { _capture(mono16k); return _inner.TranscribeAsync(mono16k, ct); }
    }
}
