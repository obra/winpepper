using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class PrerollRequestTests
{
    [Fact]
    public void ComputeRequestMs_ZeroLag_RequestsBasePreroll()
    {
        // No lag, no previous stop this process: the request is exactly the
        // single-source base constant.
        PrerollRequest.ComputeRequestMs(0, msSinceStopHotkey: null, soundsEnabled: true)
            .ShouldBe(StartCueGateMask.WarmPrerollMs);
        PrerollRequest.ComputeRequestMs(0, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(1000);
    }

    [Fact]
    public void ComputeRequestMs_TypicalLag_AddsLagOneToOne()
    {
        // M2: lag eats pre-keydown coverage 1:1 (617 ms lag event, log
        // session c9a80f2b, 2026-08-03: 617 ms lag + retrigger -> 240 ms
        // unrecorded hole), so every observed ms is requested back.
        PrerollRequest.ComputeRequestMs(30, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(1030);
        PrerollRequest.ComputeRequestMs(617, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(1617);
    }

    [Fact]
    public void ComputeRequestMs_HugeLag_ClampsToWhatTheRingCanServe()
    {
        // Observed max lag 3241 ms (annotated-log population n=1340,
        // 2026-07-29 -> 08-04); the 2 s ring can serve at most base 1000 +
        // 1000, so the lag contribution clamps. cap=1000 covers all but
        // 5/1340 sessions (0.37%); the worst event leaves up to ~2.2 s
        // uncompensated — accepted residual (see the class doc).
        PrerollRequest.ComputeRequestMs(3241, msSinceStopHotkey: null, soundsEnabled: true)
            .ShouldBe(PrerollRequest.MaxRequestMs);
        PrerollRequest.ComputeRequestMs(5000, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(2000);
    }

    [Fact]
    public void ComputeRequestMs_NegativeLag_ContributesNothing()
    {
        // Clock skew can make hook->handler deltas negative; never shrink
        // the base request because of it.
        PrerollRequest.ComputeRequestMs(-50, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(1000);
    }

    [Fact]
    public void ComputeRequestMs_RecentStop_BoundsTheReachBack()
    {
        // A1/A6 fix (2026-08-04): the ring is continuous across sessions, so
        // the request must never reach past the previous stop hotkey + the
        // stop-cue guard. min(1000 + 617, 900 - 500) = min(1617, 400) = 400.
        PrerollRequest.ComputeRequestMs(617, msSinceStopHotkey: 900, soundsEnabled: true).ShouldBe(400);
    }

    [Fact]
    public void ComputeRequestMs_TightRetrigger_RequestsNothing()
    {
        // Tight retrigger: min(1617, max(0, 300 - 500)) = 0 — everything
        // before prevStop + guard is the previous dictation or its beep.
        PrerollRequest.ComputeRequestMs(617, msSinceStopHotkey: 300, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void ComputeRequestMs_SoundsOff_BoundsAtTheStopHotkeyItself()
    {
        // No cue when sounds are off — no guard, the bound is the stop
        // hotkey itself: min(1000 + 0, 700 - 0) = 700.
        PrerollRequest.ComputeRequestMs(0, msSinceStopHotkey: 700, soundsEnabled: false).ShouldBe(700);
    }

    [Fact]
    public void ComputeRequestMs_NoPreviousStop_IsUnbounded()
    {
        // null = no previous stop this process: nothing in the ring belongs
        // to an earlier dictation -> no bound. 1000 + 30 = 1030.
        PrerollRequest.ComputeRequestMs(30, msSinceStopHotkey: null, soundsEnabled: true).ShouldBe(1030);
    }

    [Fact]
    public void ComputeRequestMs_StaleStop_HasNoEffect()
    {
        // A stop 60 s ago: min(1030, 60000 - 500) = 1030 — the bound is
        // inert for any non-retrigger start.
        PrerollRequest.ComputeRequestMs(30, msSinceStopHotkey: 60000, soundsEnabled: true).ShouldBe(1030);
    }

    [Fact]
    public void StopCueGuardMs_IsLatencyPlusCuePlusDecay()
    {
        // 200 (CueStartLatencyMarginMs) + ~150 (stop cue — stop.wav is
        // byte-equivalent to start.wav, measured 2026-08-04) + 150
        // (CueDecayMarginMs) = 500.
        PrerollRequest.StopCueGuardMs.ShouldBe(500);
    }

    [Fact]
    public void MaxRequestMs_IsBasePlusCap()
    {
        // Keep in lockstep with WarmWasapiRecorder.RingCapacitySamples (2 s):
        // MaxRequestMs must never exceed what a full ring can seed.
        PrerollRequest.MaxRequestMs.ShouldBe(
            StartCueGateMask.WarmPrerollMs + PrerollRequest.LagCompensationCapMs);
        PrerollRequest.MaxRequestMs.ShouldBe(2000);
    }
}
