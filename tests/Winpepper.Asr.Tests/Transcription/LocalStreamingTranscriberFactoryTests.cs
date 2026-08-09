using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class LocalStreamingTranscriberFactoryTests
{
    private sealed class FixedTranscriber : ITranscriber
    {
        public int Calls;
        public FixedTranscriber(string name, string text) { ModelName = name; Text = text; }
        public string ModelName { get; }
        public string Text { get; }
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { Calls++; return Task.FromResult(new TranscriptionResult(Text, ModelName)); }
    }

    [Fact]
    public void StreamingEnabled_WithEngine_BuildsNemotronStreaming()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ShouldBeOfType<NemotronStreamingTranscriber>();
        t.ModelName.ShouldBe("nemotron-streaming-en");
    }

    [Fact]
    public void StreamingEnabled_NoEngine_FallsToBatchAdapter()
    {
        var t = LocalStreamingTranscriberFactory.Build(
            () => null, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ShouldBeOfType<BatchStreamingAdapter>();
        t.ModelName.ShouldBe("nemotron-streaming-en-batch");
    }

    [Fact]
    public async Task StreamingDisabled_UsesNemotronBatch_EvenWithEngineAvailable()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: false, NullLoggerFactory.Instance);
        t.ShouldBeOfType<BatchStreamingAdapter>();
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);
        var r = await s.FinishAsync(new float[64], TestContext.Current.CancellationToken);
        r.ProviderModelName.ShouldBe("nemotron-streaming-en-batch"); // Nemotron serves streaming-off
    }

    [Fact]
    public async Task Ladder_NemotronHealthy_ParakeetNotCalled()
    {
        var engine = new FakeTranscribeCppEngine();
        var parakeet = new FixedTranscriber("parakeet-tdt-0.6b-v3", "parakeet text");
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => engine, parakeet, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        var r = await ladder.TranscribeAsync(new float[64], TestContext.Current.CancellationToken);
        r.ProviderModelName.ShouldBe("nemotron-streaming-en-batch");
        parakeet.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Ladder_NemotronUnavailable_ParakeetStepsIn()
    {
        var parakeet = new FixedTranscriber("parakeet-tdt-0.6b-v3", "parakeet text");
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => null, parakeet, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        var r = await ladder.TranscribeAsync(new float[64], TestContext.Current.CancellationToken);
        r.Text.ShouldBe("parakeet text");
        r.ProviderModelName.ShouldBe("parakeet-tdt-0.6b-v3");
    }

    [Fact]
    public async Task Ladder_NoParakeet_NemotronUnavailable_FailsLoudly()
    {
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => null, parakeetBatch: null, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        await Should.ThrowAsync<InvalidOperationException>(
            () => ladder.TranscribeAsync(new float[8], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MultilingualModel_Builds_WithNullAutodetectLanguage()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, null, "nemotron-streaming-multi", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ModelName.ShouldBe("nemotron-streaming-multi");
        // The multilingual layout's language hint is a TRUE null (autodetect
        // via the model's auto prompt slot; the literal "auto" is rejected by
        // the v0.1.3 language gate). Language plumb-through is proven
        // behaviorally in NemotronStreamingTranscriberTests via
        // BeginStreamLanguages; here the construction path is what's under test.
    }
}
