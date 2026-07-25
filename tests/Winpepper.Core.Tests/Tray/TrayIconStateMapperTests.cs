using Shouldly;
using Winpepper.Core.Tray;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.Tray;

public class TrayIconStateMapperTests
{
    [Fact]
    public void Idle_Returns_Ready_Resources()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, lastErrorMessage: null, paused: false);
        r.IconName.ShouldBe("AppIcon.ico");
        r.Tooltip.ShouldBe("Winpepper - Ready");
    }

    [Fact]
    public void Recording_Returns_Recording_Resources()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, false);
        r.IconName.ShouldBe("AppIcon-Recording.ico");
        r.Tooltip.ShouldBe("Winpepper - Recording...");
    }

    [Fact]
    public void Error_Returns_Error_Icon_And_Includes_Message_In_Tooltip()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Error, "Mic unavailable", false);
        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldContain("Mic unavailable");
    }

    [Fact]
    public void Paused_Overrides_Stage()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, paused: true);
        r.Tooltip.ShouldBe("Winpepper - Paused");
        r.IconName.ShouldBe("AppIcon.ico");
    }

    [Fact]
    public void ActiveCondition_Owns_The_Tray_While_Idle()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, lastErrorMessage: null, paused: false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldContain("Element not found.");
    }

    [Theory]
    [InlineData(SessionStage.Recording, "AppIcon-Recording.ico", "Winpepper - Recording...")]
    [InlineData(SessionStage.Transcribing, "AppIcon-Loading.ico", "Winpepper - Transcribing...")]
    [InlineData(SessionStage.CleaningUp, "AppIcon-Loading.ico", "Winpepper - Cleaning up...")]
    [InlineData(SessionStage.Injecting, "AppIcon-Loading.ico", "Winpepper - Inserting...")]
    public void ActiveCondition_Yields_To_Every_In_Flight_Dictation_Stage(
        SessionStage stage, string expectedIcon, string expectedTooltip)
    {
        var r = TrayIconStateMapper.Map(stage, null, false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe(expectedIcon);
        r.Tooltip.ShouldBe(expectedTooltip);
        r.Tooltip.ShouldNotContain("Element not found.");
    }

    /// <summary>
    /// The state the app is in for the whole attention-grab window of every
    /// CONDITION: the pill has been seized (stage == Error) while the condition
    /// is still true. The condition arm MUST be evaluated before the Error arm,
    /// or the tray would report the stale EVENT text instead of the ongoing
    /// condition. Distinct strings for the two inputs make the ordering the only
    /// thing this test can be passing on.
    /// </summary>
    [Fact]
    public void ActiveCondition_Outranks_The_Error_Stage_Arm()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Error,
            lastErrorMessage: "a stale transient failure", paused: false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldBe("Winpepper - Element not found.");
        r.Tooltip.ShouldNotContain("a stale transient failure");
        r.Tooltip.ShouldNotContain("Error:");
    }

    /// <summary>PendingPaste is a waiting state, not a dictation, so a condition
    /// still owns the tray there - the counterpart to the in-flight theory.</summary>
    [Fact]
    public void ActiveCondition_Owns_The_Tray_While_PendingPaste()
    {
        var r = TrayIconStateMapper.Map(SessionStage.PendingPaste, null, false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldBe("Winpepper - Element not found.");
    }

    [Fact]
    public void Whitespace_Condition_Is_Treated_As_No_Condition()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Error, "a real error", false,
            activeConditionMessage: "   ");

        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldBe("Winpepper - Error: a real error");
    }

    [Fact]
    public void Paused_Still_Overrides_An_Active_Condition()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, null, paused: true,
            activeConditionMessage: "Element not found.");

        r.Tooltip.ShouldBe("Winpepper - Paused");
        r.IconName.ShouldBe("AppIcon.ico");
    }

    [Fact]
    public void No_Condition_Leaves_Idle_Reporting_Ready()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, "an old event error", false,
            activeConditionMessage: null);

        r.IconName.ShouldBe("AppIcon.ico");
        r.Tooltip.ShouldBe("Winpepper - Ready");
    }
}
