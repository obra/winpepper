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

    [Fact]
    public void ActiveCondition_Yields_To_A_Live_Dictation()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Recording.ico");
        r.Tooltip.ShouldBe("Winpepper - Recording...");
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
