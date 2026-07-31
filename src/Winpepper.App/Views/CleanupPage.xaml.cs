#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class CleanupPage : Page
{
    private CleanupSettingsViewModel? _vm;

    public CleanupPage() { InitializeComponent(); }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupSettingsViewModel.PromptSettingsSupported))
            ApplyModelCapabilities();
    }

    private void ApplyModelCapabilities()
    {
        if (_vm is not { } vm) return;
        var supported = vm.PromptSettingsSupported;
        // Gray out (never hide, never clear) the channels a raw-io model ignores.
        ProfileCombo.IsEnabled = supported;
        CustomPromptBox.IsEnabled = supported;
        WindowCtxSwitch.IsEnabled = supported;
        ModelHonestyNote.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
        WindowCtxHonestyNote.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_vm is { } vm) vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
        base.OnNavigatedFrom(e);
    }

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

        _vm = vm;
        vm.RefreshModelCapabilities();   // selection may have changed while away
        ApplyModelCapabilities();
        // -=/+= pair keeps re-navigation from stacking handlers on the durable VM
        // (the existing control-lambda re-subscription wart is page-local; this VM
        // outlives the page, so be exact here).
        vm.PropertyChanged -= OnVmPropertyChanged;
        vm.PropertyChanged += OnVmPropertyChanged;
    }
}
#endif
