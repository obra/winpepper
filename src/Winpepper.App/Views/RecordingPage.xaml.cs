#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Audio;

namespace Winpepper.App.Views;

public sealed partial class RecordingPage : Page
{
    private AppShell? _shell;
    private WasapiRecorder? _levelRecorder;

    public RecordingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        _shell = shell;
        var vm = shell.RecordingVm;

        HoldBox.SetChord(vm.HoldHotkey, vm.HoldHotkeyConflict);
        ToggleBox.SetChord(vm.ToggleHotkey, vm.ToggleHotkeyConflict);

        void ApplyHotkeysIfValid()
        {
            if (vm.HoldHotkeyConflict is null && vm.ToggleHotkeyConflict is null)
                shell.Pipeline.UpdateHotkeys(vm.HoldHotkey, vm.ToggleHotkey);
        }

        HoldBox.ChordRecorded += chord =>
        {
            vm.HoldHotkey = chord;
            ApplyHotkeysIfValid();
        };
        ToggleBox.ChordRecorded += chord =>
        {
            vm.ToggleHotkey = chord;
            ApplyHotkeysIfValid();
        };
        HoldBox.CaptureRequested = shell.Pipeline.BeginHotkeyCapture;
        ToggleBox.CaptureRequested = shell.Pipeline.BeginHotkeyCapture;
        vm.PropertyChanged += (_, _) =>
        {
            HoldBox.SetChord(vm.HoldHotkey, vm.HoldHotkeyConflict);
            ToggleBox.SetChord(vm.ToggleHotkey, vm.ToggleHotkeyConflict);
        };

        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);
        MicCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == vm.MicDeviceId)
                                 ?? devices.FirstOrDefault(d => d.IsDefault);
        MicCombo.SelectionChanged += (_, _) =>
        {
            if (MicCombo.SelectedItem is CaptureDevice d) vm.MicDeviceId = d.Id;
            RestartLevelMeter(vm.MicDeviceId);
        };

        SoundsToggle.IsOn = vm.PlaySounds;
        SoundsToggle.Toggled += (_, _) => vm.PlaySounds = SoundsToggle.IsOn;
        SpeakerFilterToggle.IsOn = vm.SpeakerFilterEnabled;
        SpeakerFilterToggle.Toggled += (_, _) => vm.SpeakerFilterEnabled = SpeakerFilterToggle.IsOn;
        PostPasteLearningToggle.IsOn = vm.PostPasteLearningEnabled;
        PostPasteLearningToggle.Toggled += (_, _) => vm.PostPasteLearningEnabled = PostPasteLearningToggle.IsOn;
        PrewarmMicToggle.IsOn = vm.PrewarmMicEnabled;
        PrewarmMicToggle.Toggled += (_, _) => vm.PrewarmMicEnabled = PrewarmMicToggle.IsOn;

        // Autostart state lives in HKCU\...\Run ONLY (the MSI seeds it; the
        // toggle reads/writes it). There is deliberately no settings.json
        // mirror: a write-only shadow drifts (fresh install: key ON, old
        // setting false) and nothing ever read it.
        AutostartToggle.IsOn = _shell.Autostart.IsEnabled();
        AutostartToggle.Toggled += (_, _) =>
        {
            if (AutostartToggle.IsOn)
            {
                // Spec §7.7: the Run-key value points at the installed exe. The
                // MSI is a per-user install under %LOCALAPPDATA%\Programs\Winpepper,
                // so build that path from LocalApplicationData. In dev / on the VM
                // you can override via the WINPEPPER_AUTOSTART_EXE env var.
                var exe = Environment.GetEnvironmentVariable("WINPEPPER_AUTOSTART_EXE");
                if (string.IsNullOrEmpty(exe))
                    exe = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                          + @"\Programs\Winpepper\winpepper.exe";
                _shell.Autostart.Enable(exe, "--tray");
            }
            else _shell.Autostart.Disable();
        };

        RestartLevelMeter(vm.MicDeviceId);
    }

    private void RestartLevelMeter(string deviceId)
    {
        _levelRecorder?.Dispose();
        _levelRecorder = new WasapiRecorder(string.IsNullOrEmpty(deviceId) ? null : deviceId);
        _levelRecorder.FramesAvailable += frames =>
        {
            float peak = 0;
            for (var i = 0; i < frames.Length; i++) { var v = Math.Abs(frames.Span[i]); if (v > peak) peak = v; }
            DispatcherQueue.TryEnqueue(() => LevelMeter.Value = Math.Min(1.0, peak));
        };
        try { _levelRecorder.Start(); } catch { /* device unavailable; meter stays at zero */ }
    }

    private void OnFocusTestBox(object sender, RoutedEventArgs e) => TestBox.Focus(FocusState.Programmatic);

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _levelRecorder?.Dispose();
        HoldBox.CancelCapture("navigated away");
        ToggleBox.CancelCapture("navigated away");
    }
}
#endif
