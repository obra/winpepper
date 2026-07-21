#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Audio;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class OnboardingPage : Page
{
    private AppShell? _shell;
    private OnboardingViewModel? _vm;
    private WasapiRecorder? _meterRecorder;

    public OnboardingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        _shell = shell;
        // Real downloader: fetch ONLY the selected models that are missing,
        // using the same resolver + downloader as the Models tab (spec 3(iv)).
        // If nothing is missing this returns immediately and the step
        // auto-resolves.
        async Task RunOnboardingDownloadAsync()
        {
            var s = shell.Settings;
            var names = new[] { s.AsrModelName, s.CleanupModelName };
            var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
                shell.ModelsServices.Registry.All, shell.ModelsServices.ModelsRoot, names);
            foreach (var descriptor in missing)
            {
                var progress = new Progress<Winpepper.Models.DownloadProgress>();
                await shell.ModelsServices.DownloadAsync(
                    descriptor, shell.ModelsServices.ModelsRoot, progress, CancellationToken.None);
            }
        }

        _vm = new OnboardingViewModel(shell.SettingsWriter, RunOnboardingDownloadAsync,
                                       new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator());

        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);

        // Hydrate from persisted settings and start at the first unresolved
        // step (spec 3). persistedMicPresent: the saved device still exists in
        // the current enumeration. modelsResolved: no selected model missing.
        var settings = shell.Settings;
        var persistedMicPresent = !string.IsNullOrEmpty(settings.MicDeviceId)
                                   && devices.Any(d => d.Id == settings.MicDeviceId);
        var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
            shell.ModelsServices.Registry.All,
            shell.ModelsServices.ModelsRoot,
            new[] { settings.AsrModelName, settings.CleanupModelName });
        var modelsResolved = missing.Count == 0;
        _vm.InitializeFrom(settings, persistedMicPresent, modelsResolved);

        // Reflect the hydrated device selection in the combo.
        MicCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == settings.MicDeviceId);
        MicCombo.SelectionChanged += (_, _) =>
        {
            if (MicCombo.SelectedItem is CaptureDevice d)
            {
                _vm.SelectedMicDeviceId = d.Id;
                RestartLevelMeter(d.Id);
            }
            RefreshButtons();
        };

        void ApplyHotkeysIfValid()
        {
            if (_vm.HoldHotkeyError is null && _vm.ToggleHotkeyError is null)
                shell.Pipeline.UpdateHotkeys(_vm.HoldHotkey, _vm.ToggleHotkey);
        }

        HoldBox.ChordRecorded += chord =>
        {
            _vm.HoldHotkey = chord;
            HoldBox.SetChord(chord, _vm.HoldHotkeyError);
            ApplyHotkeysIfValid();
            RefreshButtons();
        };
        ToggleBox.ChordRecorded += chord =>
        {
            _vm.ToggleHotkey = chord;
            ToggleBox.SetChord(chord, _vm.ToggleHotkeyError);
            ApplyHotkeysIfValid();
            RefreshButtons();
        };
        HoldBox.RecordingStateChanged += shell.Pipeline.SetHotkeyCaptureActive;
        ToggleBox.RecordingStateChanged += shell.Pipeline.SetHotkeyCaptureActive;
        HoldBox.SetChord(_vm.HoldHotkey, _vm.HoldHotkeyError);
        ToggleBox.SetChord(_vm.ToggleHotkey, _vm.ToggleHotkeyError);

        TestDoneCheck.Checked   += (_, _) => { _vm.TestDictationDone = true; RefreshButtons(); };
        TestDoneCheck.Unchecked += (_, _) => { _vm.TestDictationDone = false; RefreshButtons(); };

        _vm.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RenderStep);
        RenderStep();
    }

    private async void OnAdvance(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        AdvanceButton.IsEnabled = false;
        if (_vm.Step == OnboardingStep.DownloadModels)
        {
            DownloadProgress.Visibility = Visibility.Visible;
        }
        try { await _vm.AdvanceAsync(); }
        finally { AdvanceButton.IsEnabled = true; DownloadProgress.Visibility = Visibility.Collapsed; }
        if (_vm.Step == OnboardingStep.Done)
        {
            // Onboarding complete; the user can stay on the page or switch tabs.
        }
    }

    private async void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SkipAsync();
    }

    private void RenderStep()
    {
        if (_vm is null) return;
        void Show(UIElement el, OnboardingStep s) => el.Visibility = _vm.Step == s ? Visibility.Visible : Visibility.Collapsed;
        Show(PickMicPanel,   OnboardingStep.PickMic);
        Show(HotkeyPanel,    OnboardingStep.PickHotkeys);
        Show(DownloadPanel,  OnboardingStep.DownloadModels);
        Show(TestPanel,      OnboardingStep.TestDictation);
        Show(DonePanel,      OnboardingStep.Done);

        DownloadErrorText.Text = _vm.DownloadError ?? string.Empty;
        DownloadErrorText.Visibility = _vm.HasDownloadError ? Visibility.Visible : Visibility.Collapsed;

        Border Dot(int i) => i switch { 1 => StepDot1, 2 => StepDot2, 3 => StepDot3, _ => StepDot4 };
        // Prefer the theme brushes so the dots track light/dark mode and the
        // user's accent color; fall back to fixed colors if lookup fails.
        Microsoft.UI.Xaml.Media.Brush? active = null, inactive = null;
        try
        {
            active = Application.Current.Resources["AccentFillColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
            inactive = Application.Current.Resources["ControlStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
        }
        catch { /* resource missing on this OS build; use fallbacks below */ }
        active ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.SteelBlue);
        inactive ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray);
        for (var i = 1; i <= 4; i++)
            Dot(i).Background = ((int)_vm.Step) >= (i - 1) ? active : inactive;

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (_vm is null) return;
        AdvanceButton.Content = _vm.Step switch
        {
            OnboardingStep.TestDictation => "Finish",
            OnboardingStep.DownloadModels => "Download",
            _ => "Next",
        };
        AdvanceButton.IsEnabled = _vm.CanAdvance;
        SkipButton.Visibility = _vm.CanSkip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartLevelMeter(string deviceId)
    {
        _meterRecorder?.Dispose();
        _meterRecorder = new WasapiRecorder(string.IsNullOrEmpty(deviceId) ? null : deviceId);
        _meterRecorder.FramesAvailable += frames =>
        {
            float peak = 0;
            for (var i = 0; i < frames.Length; i++) { var v = Math.Abs(frames.Span[i]); if (v > peak) peak = v; }
            DispatcherQueue.TryEnqueue(() => LevelMeter.Value = Math.Min(1.0, peak));
        };
        try { _meterRecorder.Start(); } catch { /* mic unavailable in this VM */ }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _meterRecorder?.Dispose();
        _shell?.Pipeline.SetHotkeyCaptureActive(false);
    }
}
#endif
