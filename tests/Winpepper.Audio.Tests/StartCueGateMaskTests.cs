using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class StartCueGateMaskTests
{
    [Fact]
    public void ComputeMaskMs_WarmFullPreroll_AddsPrerollAndBothMargins()
    {
        // With the shipped 150 ms cue and a fully-seeded warm pre-roll:
        // 1000 + 200 + 150 + 150 = 1500 ms. Validated 2026-08-04 by padded-
        // archive replication (both frozen corpora, +500 ms silence prefixed,
        // mask 1000->1500): 0 real pass->drop, 0 silent drop->pass — see
        // docs/plans/2026-07-29-cleanup-asr-contention-evidence.md (2026-08-04
        // section). The 1000/150 here are TEST inputs, not production
        // constants — production feeds the recorder's actually-seeded pre-roll
        // and the runtime-measured WAV.
        StartCueGateMask.ComputeMaskMs(1000, 150, soundsEnabled: true).ShouldBe(1500);
        StartCueGateMask.ComputeMaskMs(1000, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.WarmPrerollMs
            + StartCueGateMask.CueStartLatencyMarginMs
            + 150
            + StartCueGateMask.CueDecayMarginMs);
    }

    [Fact]
    public void ComputeMaskMs_ColdZeroPreroll_ShrinksToMarginsPlusCue()
    {
        // Prewarm off (or fully drained ring): buffer t=0 IS the hotkey, so
        // only latency + cue + decay need masking: 0 + 200 + 150 + 150 = 500.
        // Cold-mode simulation over the archive: this preroll-aware mask
        // flips 0/91 real dictations where a fixed worst-case 1000 ms mask
        // flips 4/91 — the reason the pre-roll is plumbed per-session.
        StartCueGateMask.ComputeMaskMs(0, 150, soundsEnabled: true).ShouldBe(500);
    }

    [Fact]
    public void ComputeMaskMs_PartialPreroll_ShrinksWithTheActualSeed()
    {
        // Partially drained ring: only 300 ms actually seeded ->
        // 300 + 200 + 150 + 150 = 800. The window follows the recorder's
        // honest report, never the request.
        StartCueGateMask.ComputeMaskMs(300, 150, soundsEnabled: true).ShouldBe(800);
    }

    [Fact]
    public void ComputeMaskMs_NegativePreroll_ClampsToZeroPreroll()
    {
        StartCueGateMask.ComputeMaskMs(-50, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.ComputeMaskMs(0, 150, soundsEnabled: true));
    }

    [Fact]
    public void ComputeMaskMs_SoundsDisabled_ReturnsZero()
    {
        // PlaySounds off ⇒ the player never emits the cue ⇒ nothing to mask.
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: false).ShouldBe(0);
    }

    [Fact]
    public void ComputeMaskMs_UnmeasuredCue_ReturnsZero()
    {
        // WavDuration failed (missing/corrupt start.wav) ⇒ FAIL OPEN: the
        // gate behaves exactly as it did before the mask existed.
        StartCueGateMask.ComputeMaskMs(500, 0, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void ComputeMaskMs_NegativeCueDuration_ReturnsZero()
    {
        StartCueGateMask.ComputeMaskMs(500, -5, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void WarmPrerollMs_IsThePipelinesPrerollRequest()
    {
        // Pin the single-source contract: PipelineHost passes THIS constant to
        // StartSession(includePrerollMs:) at both hotkey arms and feeds the
        // RETURNED actual pre-roll back into ComputeMaskMs. If this value
        // changes, the request follows automatically — that is the point.
        // The request is 1000 ms, raised 2026-08-04 from 500 — speech begun
        // >500 ms before the hotkey was never recorded, confirmed instance 8ec9e52c.
        StartCueGateMask.WarmPrerollMs.ShouldBe(1000);
    }

    [Fact]
    public void ComputeCueBudgetMs_MeasuredCue_DeductsCueWorthMinusMargin()
    {
        // Budget = measured cue - CueBudgetMarginMs = 150 - 50 = 100 ms.
        // Archive sweep 2026-08-03 (two frozen corpora, budget 0..400 ms in
        // 20 ms steps): the window satisfying ALL criteria (4/4 regression
        // WAVs pass, 0 real-dictation flips, 0 drop->pass, both beep/cue
        // escapes drop) is 100..120 ms; 100 maximizes the regression-side
        // margin (binding clip 003777a1: clear 120 vs the 100 ms floor).
        StartCueGateMask.ComputeCueBudgetMs(150, soundsEnabled: true).ShouldBe(100);
        StartCueGateMask.ComputeCueBudgetMs(150, true)
            .ShouldBe(150 - StartCueGateMask.CueBudgetMarginMs);
    }

    [Fact]
    public void ComputeCueBudgetMs_LongerCueAsset_ScalesWithMeasuredDuration()
    {
        // The asset may change or become user-configurable (owner
        // requirement): the budget must track the MEASURED duration, never
        // a constant. 300 ms asset => 300 - 50 = 250.
        StartCueGateMask.ComputeCueBudgetMs(300, soundsEnabled: true).ShouldBe(250);
    }

    [Fact]
    public void ComputeCueBudgetMs_SoundsDisabled_ReturnsZero()
    {
        // No cue was emitted => nothing to deduct (mirrors ComputeMaskMs).
        StartCueGateMask.ComputeCueBudgetMs(150, soundsEnabled: false).ShouldBe(0);
    }

    [Fact]
    public void ComputeCueBudgetMs_UnmeasuredCue_ReturnsZero()
    {
        // FAIL OPEN like the mask: unmeasured (0) or nonsense (negative)
        // cue duration => no deduction, gate behaves as before the mask.
        StartCueGateMask.ComputeCueBudgetMs(0, soundsEnabled: true).ShouldBe(0);
        StartCueGateMask.ComputeCueBudgetMs(-5, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void ComputeCueBudgetMs_TinyCue_ClampsToZero()
    {
        // A cue shorter than the margin deducts nothing: max(40 - 50, 0).
        // Safe: a <=40 ms beep can never reach the 100 ms clear floor.
        StartCueGateMask.ComputeCueBudgetMs(40, soundsEnabled: true).ShouldBe(0);
    }
}
