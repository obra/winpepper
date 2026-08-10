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
        // Picker-era wiring: the VM owns picker state and drives background
        // downloads through the shell-lifetime OnboardingProvisioner; the
        // catalog carries registry facts (names + bytes) as plain data
        // because Core has no Models reference by design. Our hydration/
        // skip-resolved behavior lives below in InitializeFrom.
        var registry = shell.ModelsServices.Registry;
        var catalog = new ModelPickerCatalog(
            Winpepper.Models.ModelRegistry.StreamingAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.StreamingAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.MultilingualStreamingAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.MultilingualStreamingAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.DefaultAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.DefaultAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.DefaultCleanupName,
            registry.Find(Winpepper.Models.ModelRegistry.DefaultCleanupName)!.TotalSizeBytes);
        _vm = new OnboardingViewModel(
            shell.SettingsWriter,
            shell.Pipeline.TryStart,
            new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator(),
            shell.OnboardingProvisioner,
            catalog);

        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);

        // Hydrate from persisted settings and start at the first unresolved
        // step (spec 3). persistedMicPresent: the saved device still exists in
        // the current enumeration. modelsResolved: no selected model missing.
        var settings = shell.Settings;
        var persistedMicPresent = !string.IsNullOrEmpty(settings.MicDeviceId)
                                   && devices.Any(d => d.Id == settings.MicDeviceId);
        var scope = new List<string> { settings.StreamingModelName };
        if (settings.OnboardingBackupModelChosen) scope.Add(settings.AsrModelName);
        if (settings.OnboardingCleanupModelChosen) scope.Add(settings.CleanupModelName);
        var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
            shell.ModelsServices.Registry.All, shell.ModelsServices.ModelsRoot, scope);
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

        EnglishRadio.Checked      += (_, _) => { _vm.MultilingualSelected = false; };
        MultilingualRadio.Checked += (_, _) => { _vm.MultilingualSelected = true; };
        BackupCheck.Checked   += (_, _) => { _vm.BackupModelSelected = true; };
        BackupCheck.Unchecked += (_, _) => { _vm.BackupModelSelected = false; };
        CleanupCheck.Checked   += (_, _) => { _vm.CleanupModelSelected = true; };
        CleanupCheck.Unchecked += (_, _) => { _vm.CleanupModelSelected = false; };
        EnglishSizeText.Text = OnboardingViewModel.SizeLabel(catalog.EnglishBytes);
        MultilingualSizeText.Text = OnboardingViewModel.SizeLabel(catalog.MultilingualBytes);
        BackupSizeText.Text = OnboardingViewModel.SizeLabel(catalog.BackupBytes);
        CleanupSizeText.Text = OnboardingViewModel.SizeLabel(catalog.CleanupBytes);
        // Hydrate picker controls from the VM (InitializeFrom ran above):
        MultilingualRadio.IsChecked = _vm.MultilingualSelected;
        EnglishRadio.IsChecked = !_vm.MultilingualSelected;
        BackupCheck.IsChecked = _vm.BackupModelSelected;
        CleanupCheck.IsChecked = _vm.CleanupModelSelected;

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
        if (_vm.Step == OnboardingStep.DownloadModels && _shell is not null)
        {
            // Publish the picked streaming model into the live slot BEFORE
            // AdvanceAsync so the engine holder and the primary-ready gate see
            // it immediately (the settings write inside AdvanceAsync is
            // durability only — the slot is the cross-thread transport,
            // exactly like ASR promote).
            _shell.StreamingModelSelection.Publish(_vm.SelectedSpeechModelName);
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
            OnboardingStep.DownloadModels => "Download & continue",
            _ => "Next",
        };
        AdvanceButton.IsEnabled = _vm.CanAdvance;
        TotalDownloadText.Text = _vm.TotalDownloadText;
        DownloadProgress.Value = _vm.DownloadProgressPercent;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Visibility = _vm.Step == OnboardingStep.TestDictation && !_vm.SpeechModelVerified
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadStatusText.Text = _vm.DownloadStatus;
        RetryDownloadButton.Visibility = _vm.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        DownloadErrorText.Text = _vm.DownloadError ?? "";
        DownloadErrorText.Visibility = _vm.DownloadError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRetryDownload(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _vm?.RetryDownloads();

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
