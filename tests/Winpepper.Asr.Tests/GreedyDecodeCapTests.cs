using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class GreedyDecodeCapTests
{
    [Fact]
    public void AdvanceSameTokenRun_SameToken_Increments()
    {
        ParakeetSession.AdvanceSameTokenRun(bestToken: 5, runTokenId: 5, sameTokenRun: 2)
            .ShouldBe((5, 3));
    }

    [Fact]
    public void AdvanceSameTokenRun_DifferentToken_ResetsToOne()
    {
        ParakeetSession.AdvanceSameTokenRun(bestToken: 7, runTokenId: 5, sameTokenRun: 3)
            .ShouldBe((7, 1));
    }

    [Fact]
    public void ShouldForceFrameAdvance_BlankToken_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 99, blankId: 99, emitted: 0, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_PerFrameEmitCapHit_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 10, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_SameTokenRunCapHit_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 3, maxTokensPerStep: 10,
            sameTokenRun: 3, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_NormalDecode_False()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 1, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeFalse();
    }

    // A stuck frame (bestDur == 0) that keeps arg-maxing the SAME non-blank
    // token must emit at most MaxSameTokenRun copies before the loop forces the
    // frame to advance. Proves the cap in isolation with no ONNX model, using
    // the exact same two helpers the decode loop uses.
    [Fact]
    public void StuckFrameSprayingSameToken_CappedAtMaxSameTokenRun()
    {
        const int blankId = 99, stuckToken = 3, maxPerStep = 10, maxRun = 3;
        var runTokenId = blankId;
        var sameTokenRun = 0;
        var emitted = 0;
        var emissions = 0;
        var advanced = false;

        for (var step = 0; step < 20 && !advanced; step++)
        {
            // token emitted this decode step
            emitted++;
            emissions++;
            (runTokenId, sameTokenRun) =
                ParakeetSession.AdvanceSameTokenRun(stuckToken, runTokenId, sameTokenRun);

            // bestDur == 0 branch: does the loop force an advance now?
            if (ParakeetSession.ShouldForceFrameAdvance(
                    stuckToken, blankId, emitted, maxPerStep, sameTokenRun, maxRun))
            {
                advanced = true;
            }
        }

        advanced.ShouldBeTrue();
        emissions.ShouldBe(maxRun); // exactly 3 copies, then forced advance
    }

    // A legitimate repeated word ("no no no") emits the same token across
    // DIFFERENT frames, each advancing via a positive duration. The run counter
    // resets on every frame advance, so the cap never fires.
    [Fact]
    public void RepeatedWordAcrossFrames_NeverCapped()
    {
        const int blankId = 99, wordToken = 3, maxPerStep = 10, maxRun = 3;
        var runTokenId = blankId;
        var sameTokenRun = 0;
        var capped = false;

        // Three frames; each emits "wordToken" once, then advances (bestDur > 0).
        for (var frame = 0; frame < 3; frame++)
        {
            var emitted = 1;
            (runTokenId, sameTokenRun) =
                ParakeetSession.AdvanceSameTokenRun(wordToken, runTokenId, sameTokenRun);

            if (ParakeetSession.ShouldForceFrameAdvance(
                    wordToken, blankId, emitted, maxPerStep, sameTokenRun, maxRun))
            {
                capped = true;
            }

            // bestDur > 0 advance resets the per-frame run tracking.
            sameTokenRun = 0;
            runTokenId = blankId;
        }

        capped.ShouldBeFalse();
    }
}
