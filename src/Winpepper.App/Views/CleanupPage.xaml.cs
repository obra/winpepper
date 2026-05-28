#if WINDOWS
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;

namespace Winpepper.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        var vm = shell.CleanupVm;

        EnabledSwitch.IsOn = vm.Enabled;
        EnabledSwitch.Toggled += (_, _) => vm.Enabled = EnabledSwitch.IsOn;

        WindowCtxSwitch.IsOn = vm.WindowContextEnabled;
        WindowCtxSwitch.Toggled += (_, _) => vm.WindowContextEnabled = WindowCtxSwitch.IsOn;

        ProfileCombo.SelectedValue = vm.Profile;
        ProfileCombo.SelectionChanged += (_, _) =>
        {
            if (ProfileCombo.SelectedValue is string s) vm.Profile = s;
            CustomPromptBox.IsReadOnly = !vm.CustomPromptEditable;
        };
        CustomPromptBox.IsReadOnly = !vm.CustomPromptEditable;
        CustomPromptBox.Text = vm.CustomPrompt;
        CustomPromptBox.TextChanged += (_, _) => vm.CustomPrompt = CustomPromptBox.Text;

        MaxTokSlider.Value = vm.MaxNewTokens;
        MaxTokLabel.Text = $"Max new tokens: {vm.MaxNewTokens}";
        MaxTokSlider.ValueChanged += (_, _) =>
        {
            vm.MaxNewTokens = (int)MaxTokSlider.Value;
            MaxTokLabel.Text = $"Max new tokens: {vm.MaxNewTokens}";
        };

        TimeoutSlider.Value = vm.TimeoutMs;
        TimeoutLabel.Text = $"Timeout: {vm.TimeoutMs} ms";
        TimeoutSlider.ValueChanged += (_, _) =>
        {
            vm.TimeoutMs = (int)TimeoutSlider.Value;
            TimeoutLabel.Text = $"Timeout: {vm.TimeoutMs} ms";
        };
    }
}
#endif
