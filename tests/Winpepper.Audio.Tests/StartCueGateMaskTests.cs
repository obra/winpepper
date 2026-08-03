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
        // 500 + 200 + 150 + 150 = 1000 ms. Re-validated 2026-08-02/03 with
        // the plan's exact dual-threshold semantics over the frozen
        // 100-recording archive: 0/91 real pass->drop, 0/6 drop->pass at
        // this window; tightest passer margin 140 ms. The 500/150 here are
        // TEST inputs, not production constants — production feeds the
        // recorder's actually-seeded pre-roll and the runtime-measured WAV.
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(1000);
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(
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
        StartCueGateMask.WarmPrerollMs.ShouldBe(500);
    }
}
