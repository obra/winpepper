using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class MidPasteDeciderTests
{
    [Fact]
    public void SameHwnd_Continues()
    {
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 42)
            .ShouldBe(MidPasteDecision.Continue);
    }

    [Fact]
    public void DifferentHwnd_Halts()
    {
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Halt);
    }

    [Fact]
    public void ZeroBaseline_Halts_FailSafe()
    {
        // DELIBERATE PIN REVISION (council, probe-gated 2026-07-28,
        // supersedes the midpaste-focus-fallback fail-open pin): with no
        // baseline we cannot know the foreground is still the user's chosen
        // target, and typing blind can silently lose text. Halt parks the
        // FULL text. In production a 0 baseline no longer reaches this
        // decider (TextInjector parks at start first) -- this arm is the
        // fail-safe default for any direct caller.
        MidPasteDecider.Decide(hwndAtSendStart: 0, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Halt);
    }

    [Fact]
    public void ZeroCurrent_Halts_FailSafe()
    {
        // The per-chunk probe read 0 mid-run (focus transition, lock screen,
        // secure desktop / UAC prompt): exactly the dangerous moment
        // (unanimous council finding). Stop typing; the caller parks the
        // FULL text and one pill click recovers it.
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 0)
            .ShouldBe(MidPasteDecision.Halt);
    }
}
