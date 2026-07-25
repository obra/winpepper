using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class TdtGreedyDecoderTests
{
    private static EncoderOutput Enc(int frames)
    {
        const int dim = 2;
        var data = new float[dim * frames];
        for (var t = 0; t < frames; t++) { data[t] = t; data[frames + t] = t; }
        return new EncoderOutput(data, frames, dim, frames);
    }

    private static TdtDecoderState NewState(FakeParakeetBackend b)
        => new(b.DecoderHiddenLayers, b.DecoderHiddenDim, b.BlankId);

    [Fact]
    public void Blank_AdvancesOneFrame_WithoutEmitting()
    {
        var backend = new FakeParakeetBackend(); // default: blank, dur 1
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(4), NewState(backend), tokens, fi, du);
        tokens.ShouldBeEmpty();
        backend.JointCalls.Count.ShouldBe(4);
    }

    [Fact]
    public void NonBlank_EmitsToken_AndAdoptsState()
    {
        var backend0 = new FakeParakeetBackend();
        var newH = new float[8]; newH[0] = 42f;
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => last == 7 /*blank start*/
                ? backend0.Emit(3, 1, h: newH)
                : backend0.Emit(7, 1),
        };
        var state = NewState(backend);
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du);
        tokens.ShouldBe(new[] { 3 });
        fi.ShouldBe(new[] { 0 });
        state.LastToken.ShouldBe(3);
        state.StateH[0].ShouldBe(42f);
    }

    [Fact]
    public void DurationHead_SkipsFrames()
    {
        var backend0 = new FakeParakeetBackend();
        var calls = 0;
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => { calls++; return backend0.Emit(2, 3); }, // always dur 3
        };
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(9), NewState(backend), tokens, fi, du);
        calls.ShouldBe(3);                 // frames 0, 3, 6
        fi.ShouldBe(new[] { 0, 3, 6 });
        du.ShouldBe(new[] { 3, 3, 3 });
    }

    [Fact]
    public void ZeroDuration_EmissionsCappedByMaxTokensPerStep()
    {
        var backend0 = new FakeParakeetBackend();
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => backend0.Emit(1, 0), // emit forever at frame 0
        };
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(1), NewState(backend), tokens, fi, du);
        tokens.Count.ShouldBe(TdtGreedyDecoder.MaxTokensPerStep);
    }

    [Fact]
    public void CarryAdvance_ContinuesTheSkipIntoTheNextSegment()
    {
        var backend0 = new FakeParakeetBackend();
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => backend0.Emit(2, 4), // dur 4 from frame 0
        };
        var state = NewState(backend);
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();

        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du); // t: 0 -> 4, limit 3
        state.CarryAdvance.ShouldBe(1);

        backend.JointCalls.Clear();
        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du, frameIndexOffset: 3);
        backend.JointCalls.Count.ShouldBe(1);       // starts at local frame 1 (carry), jumps past end
        fi[1].ShouldBe(4);                          // global index = offset 3 + local 1
    }

    [Fact]
    public void StartFrame_SkipsDiscardedContextFrames()
    {
        var backend = new FakeParakeetBackend(); // blank, dur 1
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(6), NewState(backend), tokens, fi, du, startFrame: 2);
        backend.JointCalls.Count.ShouldBe(4); // frames 2..5
    }
}
