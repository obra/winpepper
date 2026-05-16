using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class CleanupSettingsViewModelTests
{
    [Fact]
    public void Defaults_Map_From_Contract()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.Enabled.ShouldBeTrue();
        vm.WindowContextEnabled.ShouldBeFalse();
        vm.Profile.ShouldBe("Ordinary");
        vm.MaxNewTokens.ShouldBe(512);
        vm.TimeoutMs.ShouldBe(15000);
    }

    [Fact]
    public void Setting_MaxNewTokens_Clamps_To_Min_64_Max_4096()
    {
        CleanupSettingsContract? last = null;
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), s => last = s);
        vm.MaxNewTokens = 10;
        vm.MaxNewTokens.ShouldBe(64);
        vm.MaxNewTokens = 10_000;
        vm.MaxNewTokens.ShouldBe(4096);
        last!.MaxNewTokens.ShouldBe(4096);
    }

    [Fact]
    public void Setting_TimeoutMs_Clamps_To_Min_2000_Max_60000()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.TimeoutMs = 500;
        vm.TimeoutMs.ShouldBe(2000);
        vm.TimeoutMs = 999_999;
        vm.TimeoutMs.ShouldBe(60000);
    }

    [Fact]
    public void Setting_Profile_To_Custom_Allows_Editing_CustomPrompt()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.Profile = "Custom";
        vm.CustomPromptEditable.ShouldBeTrue();
        vm.Profile = "Ordinary";
        vm.CustomPromptEditable.ShouldBeFalse();
    }

    [Fact]
    public void Property_Set_Invokes_Persist_Callback()
    {
        var calls = 0;
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => calls++);
        vm.Enabled = false;
        calls.ShouldBe(1);
    }
}
