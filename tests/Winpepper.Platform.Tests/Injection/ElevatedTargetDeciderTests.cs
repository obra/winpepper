using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class ElevatedTargetDeciderTests
{
    [Fact]
    public void KnownHwnd_Elevated_Parks()
    {
        // The one new behavior: foreground positively observable AND elevated
        // => never inject (UIPI would silently drop every keystroke while
        // reporting success); park the full text instead.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Park);
    }

    [Fact]
    public void KnownHwnd_NotElevated_Injects()
    {
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.NotElevated)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }

    [Fact]
    public void KnownHwnd_UnknownElevation_Injects_FailOpen()
    {
        // Transient observation failure (window died mid-probe, probe threw):
        // preserve today's behavior -- inject. Same fail-open bias as
        // MidPasteDecider / PendingPasteDecider / MouseButtonGuard.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.Unknown)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }

    [Fact]
    public void ZeroHwnd_Parks_FailSafe_RegardlessOfProbeResult()
    {
        // DELIBERATE PIN REVISION (council 5-1, probe-gated 2026-07-28,
        // supersedes the paste-path-hardening fail-open pin): an absent
        // foreground hwnd now PARKS instead of blind-injecting. Normally
        // unreachable -- TextInjector returns NoForeground before consulting
        // this decider -- kept as defense in depth. Contrast the UNCHANGED
        // fail-open next door: a KNOWN hwnd with an unobservable elevation
        // probe still injects (KnownHwnd_UnknownElevation_Injects_FailOpen).
        ElevatedTargetDecider.Decide(hwndAtSendStart: 0, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Park);
    }
}
