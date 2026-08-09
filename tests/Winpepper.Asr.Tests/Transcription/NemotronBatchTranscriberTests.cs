using Shouldly;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class NemotronBatchTranscriberTests
{
    [Fact]
    public async Task Transcribes_ViaEngineBatch_WithLanguageHint_AndReportsItsOwnModelName()
    {
        var engine = new FakeTranscribeCppEngine();
        // Pass-through proof with a realistic locale (autodetect is a TRUE
        // null hint, not the string "auto" — the v0.1.3 gate rejects "auto").
        var t = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-multi-batch", language: "en-US");

        var result = await t.TranscribeAsync(new float[128], TestContext.Current.CancellationToken);

        result.ProviderModelName.ShouldBe("nemotron-streaming-multi-batch");
        result.Text.ShouldNotBeNull();
        engine.LastBatchLanguage.ShouldBe("en-US");
    }

    [Fact]
    public async Task NullEngine_Throws_InvalidOperation()
    {
        var t = new NemotronBatchTranscriber(() => null, "nemotron-streaming-en-batch");
        await Should.ThrowAsync<InvalidOperationException>(
            () => t.TranscribeAsync(new float[4], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreCancelledToken_DoesNotTouchTheEngine()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = new FakeTranscribeCppEngine();
        var t = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-en-batch");
        await Should.ThrowAsync<OperationCanceledException>(() => t.TranscribeAsync(new float[4], cts.Token));
    }
}
