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
    public async Task InteriorSilence_LongRun_NotFedToTheEncoder()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // Speech, then ~4 s of interior silence (past the 1200 ms keep budget),
        // then speech. The skipper keeps 600 ms per edge (Hop*60 samples each)
        // and drops the 2800 ms middle before the mel extractor.
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);
        await session.PushAsync(new float[Hop * 400], TestContext.Current.CancellationToken);
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);
        await session.FinishAsync(Audio(Hop * 520), TestContext.Current.CancellationToken);

        // NEW mel frames encoded (subtract the 20 re-encoded context frames per
        // follow-up encode): kept audio is Hop*240 samples -> 241 frames, far
        // below the ungated Hop*520 -> ~521.
        var newFrames = backend.EncodeMelFrameCounts[0]
            + backend.EncodeMelFrameCounts.Skip(1).Sum(c => c - 20);
        newFrames.ShouldBe(Hop * 240 / Hop + 1); // 241
        newFrames.ShouldBeLessThan(300);
    }

    [Fact]
    public async Task InteriorSilence_ShortRun_IsKeptAndEncoded()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // 1 s of interior silence is under the 1200 ms keep budget: kept whole,
        // so the encoder sees exactly the ungated frame count.
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);
        await session.PushAsync(new float[Hop * 100], TestContext.Current.CancellationToken);
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);
        await session.FinishAsync(Audio(Hop * 220), TestContext.Current.CancellationToken);

        var newFrames = backend.EncodeMelFrameCounts[0]
            + backend.EncodeMelFrameCounts.Skip(1).Sum(c => c - 20);
        newFrames.ShouldBe(221); // Hop*220 samples / Hop + 1: nothing was skipped
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
    public async Task FinishTimeEncoderFailure_AfterSuccessfulStreaming_FallsBackToBatchOverTheFullBuffer()
    {
        var calls = 0;
        var backend = new FakeParakeetBackend
        {
            // Calls 1-2 are the push-time chunk encodes; call 3 is the tail encode
            // inside FinishAsync's Task.Run.
            OnEncode = _ => { if (++calls == 3) throw new InvalidOperationException("onnx died at finish"); },
        };
        ReadOnlyMemory<float> seen = default;
        var session = NewSession(backend,
            (audio, _) => { seen = audio; return Task.FromResult(new TranscriptionResult("BATCH", "parakeet-test")); },
            chunk: 50, context: 0);
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken); // 2 chunks stream fine

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);

        calls.ShouldBe(3);                // streaming succeeded, then the tail encode was attempted
        result.Text.ShouldBe("BATCH");    // finish-time catch landed on the batch fallback
        seen.Length.ShouldBe(Hop * 120);  // the fallback received the FULL audio buffer
    }

    [Fact]
    public async Task EncoderOutputLengthMismatch_TripsTheSubsamplingAssertion_AndFallsBackToBatch()
    {
        // With SubsamplingFactor 2 the fake returns T/2 frames: an even-length encode
        // matches floor((T-1)/2)+1 exactly, an odd-length encode returns one frame
        // fewer than the formula demands.
        var backend = new FakeParakeetBackend { SubsamplingFactor = 2 };
        ReadOnlyMemory<float> seen = default;
        var session = NewSession(backend,
            (audio, _) => { seen = audio; return Task.FromResult(new TranscriptionResult("BATCH", "parakeet-test")); },
            chunk: 50, context: 21);
        // Chunk 1 encodes T=50 (even: consistent, establishes F=2); chunk 2 encodes
        // T=21+50=71 (odd: 35 frames returned, formula demands 36) -> the session's
        // re-assertion throws inside PushAsync and latches the session corrupt.
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);

        backend.EncodeMelFrameCounts.Count.ShouldBe(2); // both encodes returned normally, no tail encode
        backend.EncodeMelFrameCounts[0].ShouldBe(50);
        backend.EncodeMelFrameCounts[1].ShouldBe(21 + 50);
        result.Text.ShouldBe("BATCH");    // yet the session fell back: the assertion itself fired
        seen.Length.ShouldBe(Hop * 120);  // over the full audio buffer
    }

    [Fact]
    public async Task SubsamplingFactorAboveOne_ContextDiscardMath_DecodesEveryEncoderFrameExactlyOnce()
    {
        // The fake returns T/2 encoder frames at factor 2; every encode below has
        // even T, where T/2 == floor((T-1)/2)+1, so the exact-form assertion holds
        // throughout and the session streams to completion.
        var backend = new FakeParakeetBackend { SubsamplingFactor = 2 };
        var fallbackCalled = false;
        var session = NewSession(backend,
            (_, _) => { fallbackCalled = true; return Task.FromResult(new TranscriptionResult("BATCH", "parakeet-test")); },
            chunk: 50, context: 20);

        // Hop*119 samples -> 120 mel frames total: chunks T=50 and T=20+50=70
        // encode during push, and the T=20+20=40 tail encodes at finish.
        await session.PushAsync(Audio(Hop * 119), TestContext.Current.CancellationToken);
        var result = await session.FinishAsync(Audio(Hop * 119), TestContext.Current.CancellationToken);

        fallbackCalled.ShouldBeFalse();  // consistent factor-2 encodes pass the re-assertion
        result.Text.ShouldBe("");        // STREAMED transcript: default joint emits only blanks
        result.ProviderModelName.ShouldBe("parakeet-test");
        backend.EncodeMelFrameCounts.ShouldBe(new[] { 50, 20 + 50, 20 + 20 });
        // Discard math observably correct: encode 1 decodes frames 0..24 (no context,
        // discard 0); encodes 2 and 3 each discard (20-1)/2+1 = 10 context encoder
        // frames, decoding 35-10=25 and 20-10=10. 25+25+10 = 60 = 120/2 joint calls:
        // every global encoder frame decoded exactly once. Discard 0 on the
        // context-bearing encodes would give 80 (double-decoded context); an
        // over-discard would give fewer than 60 (skipped frames).
        backend.JointCalls.Count.ShouldBe(60);
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
