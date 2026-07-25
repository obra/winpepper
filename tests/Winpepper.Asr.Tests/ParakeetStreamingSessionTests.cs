using Shouldly;
using Winpepper.Asr;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ParakeetStreamingSessionTests
{
    private const int Hop = 160; // PreprocessorConfig.ParakeetTdtV3.HopLength

    private static float[] Audio(int samples)
    {
        var rng = new Random(3);
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = (float)(rng.NextDouble() * 0.6 - 0.3);
        return a;
    }

    private static ParakeetStreamingSession NewSession(
        FakeParakeetBackend backend,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>>? fallback = null,
        int chunk = 50, int context = 20)
        => new(
            backend, "parakeet-test", PreprocessorConfig.ParakeetTdtV3,
            fallback ?? ((_, _) => Task.FromResult(new TranscriptionResult("BATCH", "parakeet-test"))),
            chunkMelFrames: chunk, leftContextMelFrames: context);

    [Fact]
    public async Task ZeroPushedAudio_FinishUsesTheBatchFallback()
    {
        ReadOnlyMemory<float> seen = default;
        var session = NewSession(new FakeParakeetBackend(),
            (audio, _) => { seen = audio; return Task.FromResult(new TranscriptionResult("BATCH", "m")); });

        var result = await session.FinishAsync(Audio(1234), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("BATCH");
        seen.Length.ShouldBe(1234);
    }

    [Fact]
    public async Task ShortDictation_NoChunkEncoded_UsesTheBatchFallback()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 1000); // needs ~1000 mel frames per chunk
        await session.PushAsync(Audio(Hop * 100), TestContext.Current.CancellationToken); // ~100 frames

        var result = await session.FinishAsync(Audio(Hop * 100), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("BATCH");
        backend.EncodeMelFrameCounts.ShouldBeEmpty(); // nothing streamed
    }

    [Fact]
    public async Task LongDictation_EncodesChunksDuringPush_AndOnlyTheTailAtFinish()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // ~120 mel frames of audio → two 50-frame chunks encode during push.
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        backend.EncodeMelFrameCounts.Count.ShouldBe(2);
        backend.EncodeMelFrameCounts[0].ShouldBe(50);        // first chunk: no context yet
        backend.EncodeMelFrameCounts[1].ShouldBe(20 + 50);   // context + chunk

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        backend.EncodeMelFrameCounts.Count.ShouldBe(3);      // exactly one tail encode
        backend.EncodeMelFrameCounts[2].ShouldBeLessThan(20 + 50); // tail is smaller than a full chunk
        result.ProviderModelName.ShouldBe("parakeet-test");
    }

    [Fact]
    public async Task DecoderState_CarriesAcrossChunks()
    {
        var backend0 = new FakeParakeetBackend();
        FakeParakeetBackend backend = null!;
        backend = new FakeParakeetBackend
        {
            // Emit token 2 once per segment start, then blanks — LastToken should
            // stay 2 across chunk boundaries.
            Joint = (frame, last) => last == backend.BlankId
                ? backend0.Emit(2, 1)
                : backend0.Emit(backend.BlankId, 1),
        };
        var session = NewSession(backend, chunk: 50, context: 0);
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);

        // After the first emission every later joint call must see LastToken == 2.
        backend.JointCalls.Skip(1).ShouldAllBe(call => call.LastToken == 2);
    }

    [Fact]
    public async Task LeadingSilence_IsGated_NotFedToTheEncoder()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // ~60 mel frames of pure silence, then ~60 frames of speech-level audio.
        await session.PushAsync(new float[Hop * 60], TestContext.Current.CancellationToken);
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);

        // Ungated, ~120 frames would have produced two 50-frame chunk encodes;
        // gated, only the ~60 post-onset frames exist -> exactly one encode.
        backend.EncodeMelFrameCounts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MidStreamEncoderFailure_FallsBackToBatchAtFinish()
    {
        var calls = 0;
        var backend = new FakeParakeetBackend
        {
            OnEncode = _ => { if (++calls == 2) throw new InvalidOperationException("onnx died"); },
        };
        var session = NewSession(backend, chunk: 50, context: 0);
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken); // 2nd chunk throws inside

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        result.Text.ShouldBe("BATCH");
    }

    [Fact]
    public async Task Transcriber_StartsAFreshSessionPerDictation()
    {
        var backend = new FakeParakeetBackend();
        var transcriber = new ParakeetStreamingTranscriber(
            backend, FakeTranscriber.Returning("parakeet-test", "BATCH"),
            "parakeet-test", PreprocessorConfig.ParakeetTdtV3);
        transcriber.ModelName.ShouldBe("parakeet-test");

        await using var s1 = await transcriber.StartSessionAsync(TestContext.Current.CancellationToken);
        await using var s2 = await transcriber.StartSessionAsync(TestContext.Current.CancellationToken);
        s1.ShouldNotBeSameAs(s2);
    }
}
