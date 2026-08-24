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
        vm.Enabled.ShouldBeFalse(); // 2026-08-24: cleanup LLM is opt-in
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
        vm.Enabled = true; // a real change: the default is now False (opt-in, 2026-08-24)
        calls.ShouldBe(1);
    }

    [Fact]
    public void PromptSettingsSupported_Defaults_True_Without_Delegate()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.PromptSettingsSupported.ShouldBeTrue();
    }

    [Fact]
    public void PromptSettingsSupported_Reads_Delegate_At_Construction()
    {
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), _ => { }, () => false);
        vm.PromptSettingsSupported.ShouldBeFalse();
    }

    [Fact]
    public void RefreshModelCapabilities_Raises_Only_On_Change()
    {
        var supported = false;
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), _ => { }, () => supported);
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupSettingsViewModel.PromptSettingsSupported))
                raised++;
        };

        vm.RefreshModelCapabilities();          // false -> false: no raise
        raised.ShouldBe(0);

        supported = true;
        vm.RefreshModelCapabilities();          // false -> true: raise
        vm.PromptSettingsSupported.ShouldBeTrue();
        raised.ShouldBe(1);
    }

    [Fact]
    public void Capability_Change_Never_Touches_Stored_Values()
    {
        var supported = true;
        CleanupSettingsContract? last = null;
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), s => last = s, () => supported);
        vm.Profile = "Custom";
        vm.CustomPrompt = "keep me";
        vm.WindowContextEnabled = true;

        supported = false;
        vm.RefreshModelCapabilities();

        vm.Profile.ShouldBe("Custom");
        vm.CustomPrompt.ShouldBe("keep me");
        vm.WindowContextEnabled.ShouldBeTrue();
        last.ShouldNotBeNull();
        last!.CustomPrompt.ShouldBe("keep me");
    }
}
