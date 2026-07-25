using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class PowerResumeDecisionTests
{
    [Fact]
    public void ResumeSuspend_Is_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMRESUMESUSPEND).ShouldBeTrue();

    [Fact]
    public void ResumeAutomatic_Is_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMRESUMEAUTOMATIC).ShouldBeTrue();

    [Fact]
    public void Suspend_Is_Not_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMSUSPEND).ShouldBeFalse();

    [Fact]
    public void PowerSettingChange_Is_Not_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_POWERSETTINGCHANGE).ShouldBeFalse();

    [Fact]
    public void Constants_Match_The_Win32_Values()
    {
        PowerResumeDecision.PBT_APMSUSPEND.ShouldBe(0x0004u);
        PowerResumeDecision.PBT_APMRESUMESUSPEND.ShouldBe(0x0007u);
        PowerResumeDecision.PBT_APMRESUMEAUTOMATIC.ShouldBe(0x0012u);
    }
}
