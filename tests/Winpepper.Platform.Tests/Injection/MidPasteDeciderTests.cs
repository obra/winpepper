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
    public void UnknownBaseline_Continues_FailOpen()
    {
        // Could not capture the foreground window when the send started
        // (probe failed / non-Windows). Preserve today's behavior: keep typing.
        MidPasteDecider.Decide(hwndAtSendStart: 0, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Continue);
    }

    [Fact]
    public void UnknownCurrent_Continues_FailOpen()
    {
        // Probe failed mid-run (or Windows reports no foreground window,
        // e.g. lock screen). We never halt on a failed observation.
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 0)
            .ShouldBe(MidPasteDecision.Continue);
    }
}
