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
    private CancellationTokenSource? _lifetimeCts;

    public OnboardingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        _shell = shell;
        _lifetimeCts = new CancellationTokenSource();
        // Upstream verified-provisioning wiring: ModelsServices implements
        // IAsrProvisioningService (download + verify), and the VM starts the
        // pipeline only after the model verifies. Our hydration/skip-resolved
        // behavior lives below in InitializeFrom.
        _vm = new OnboardingViewModel(
            shell.SettingsWriter,
            shell.ModelsServices,
            shell.Pipeline.TryStart,
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
        HoldBox.CaptureRequested = shell.Pipeline.BeginHotkeyCapture;
        ToggleBox.CaptureRequested = shell.Pipeline.BeginHotkeyCapture;
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
        if (_vm.Step == OnboardingStep.PickHotkeys)
        {
            HoldBox.CancelCapture("leaving hotkey step");
            ToggleBox.CancelCapture("leaving hotkey step");
        }
        if (_vm.Step == OnboardingStep.DownloadModels)
        {
            DownloadProgress.Visibility = Visibility.Visible;
        }
        try { await _vm.AdvanceAsync(_lifetimeCts?.Token ?? CancellationToken.None); }
        finally { RefreshButtons(); }
        if (_vm.Step == OnboardingStep.Done)
        {
            // Onboarding complete; the user can stay on the page or switch tabs.
        }
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
            OnboardingStep.DownloadModels when _vm.CanRetry => "Retry",
            OnboardingStep.DownloadModels => "Download speech model",
            _ => "Next",
        };
        AdvanceButton.IsEnabled = _vm.CanAdvance;
        DownloadProgress.Value = _vm.DownloadProgressPercent;
        DownloadProgress.IsIndeterminate = _vm.IsBusy && _vm.DownloadProgressPercent <= 0;
        DownloadProgress.Visibility = _vm.Step == OnboardingStep.DownloadModels && _vm.IsBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        DownloadStatusText.Text = _vm.DownloadStatus;
        DownloadErrorText.Text = _vm.DownloadError ?? "";
        DownloadErrorText.Visibility = _vm.DownloadError is null ? Visibility.Collapsed : Visibility.Visible;
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
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        _meterRecorder?.Dispose();
        _vm?.Dispose();
        HoldBox.CancelCapture("navigated away");
        ToggleBox.CancelCapture("navigated away");
    }
}
#endif
