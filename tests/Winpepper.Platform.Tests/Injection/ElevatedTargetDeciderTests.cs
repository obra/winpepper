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
    public void UnknownHwnd_Injects_FailOpen_EvenIfProbeClaimsElevated()
    {
        // No observable foreground at all (probe returned 0): the HWND guard
        // is disabled today and this check must not regress that. A probe
        // result for hwnd 0 is meaningless; fail open takes precedence.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 0, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }
}
