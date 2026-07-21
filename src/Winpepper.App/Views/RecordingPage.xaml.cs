#if WINDOWS
using Microsoft.Extensions.Logging;
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
        HoldBox.RecordingStateChanged += shell.Pipeline.SetHotkeyCaptureActive;
        ToggleBox.RecordingStateChanged += shell.Pipeline.SetHotkeyCaptureActive;
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
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = AutostartToggle.IsOn });
        };

        // Speech recognition (AssemblyAI)
        var settingsStore = shell.SettingsStore;
        var keyStore = shell.AssemblyAiKeyStore;

        var current = settingsStore.Load();

        // Provider picker
        AsrProviderCombo.SelectedIndex = string.Equals(current.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AssemblyAiPanel.Visibility = AsrProviderCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AsrProviderCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AsrProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "local";
            AssemblyAiPanel.Visibility = tag == "assemblyai" ? Visibility.Visible : Visibility.Collapsed;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AsrProvider = tag });
        };

        // Model picker: known ids + an "Advanced/custom" escape hatch.
        const string CustomTag = "__custom__";
        AssemblyAiModelCombo.Items.Clear();
        foreach (var m in Winpepper.Asr.Transcription.AssemblyAiModels.Known)
            AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = "Advanced / custom…", Tag = CustomTag });

        void SelectModelInCombo(string modelId)
        {
            // A model can be "known" via an accepted alias (e.g. "universal-3-5-pro")
            // that has no dedicated combo item; canonicalize first so either official
            // spelling resolves to the listed id, then look the item up safely. If no
            // listed item matches (truly custom id, or an alias with no listed target)
            // we fall back to the custom item rather than throwing.
            var canonical = Winpepper.Asr.Transcription.AssemblyAiModels.CanonicalId(modelId);
            var matchIndex = -1;
            for (var i = 0; i < AssemblyAiModelCombo.Items.Count; i++)
            {
                var tag = (string?)((ComboBoxItem)AssemblyAiModelCombo.Items[i]).Tag;
                if (tag != CustomTag && string.Equals(tag, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }

            var hasItem = matchIndex >= 0;
            AssemblyAiModelCombo.SelectedIndex = hasItem ? matchIndex : AssemblyAiModelCombo.Items.Count - 1; // the custom item
            var isCustom = !hasItem;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelBox.Text = isCustom ? modelId : "";
        }
        SelectModelInCombo(current.AssemblyAiModel);

        AssemblyAiModelCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AssemblyAiModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId;
            var isCustom = tag == CustomTag;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            if (!isCustom)
                _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = tag });
        };
        AssemblyAiModelBox.LostFocus += (_, _) =>
        {
            var model = string.IsNullOrWhiteSpace(AssemblyAiModelBox.Text)
                ? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId
                : AssemblyAiModelBox.Text.Trim();
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = model });
        };

        // Retention + keyterms toggles.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = AssemblyAiDeleteToggle.IsOn });
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = AssemblyAiKeytermsToggle.IsOn });

        // Key status
        AsrStatusText.Text = keyStore.HasKey ? "A key is saved on this PC." : "No key saved.";

        SaveKeyButton.Click += (_, _) =>
        {
            var key = AssemblyAiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) { AsrStatusText.Text = "Enter a key first."; return; }
            keyStore.Save(key.Trim());
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key saved on this PC.";
        };

        ClearKeyButton.Click += (_, _) =>
        {
            keyStore.Clear();
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key cleared.";
        };

        TestKeyButton.Click += async (_, _) =>
        {
            var typed = AssemblyAiKeyBox.Password;
            var hasTyped = !string.IsNullOrWhiteSpace(typed);
            if (!hasTyped && !keyStore.HasKey) { AsrStatusText.Text = "Enter or save a key before testing."; return; }

            AsrStatusText.Text = hasTyped ? "Testing the key you typed…" : "Testing the saved key…";
            try
            {
                Winpepper.Asr.Transcription.IAssemblyAiClient clientToTest = shell.AssemblyAiClient;
                if (hasTyped)
                {
                    // Validate exactly what the user typed, not a previously saved key.
                    var typedKey = typed.Trim();
                    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    clientToTest = new Winpepper.Asr.Transcription.AssemblyAiClient(
                        http, () => typedKey, shell.AssemblyAiOptions,
                        shell.LogFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ok = await clientToTest.ValidateKeyAsync(cts.Token);
                if (ok && hasTyped)
                {
                    keyStore.Save(typed.Trim());          // typed key is valid -> save it
                    AssemblyAiKeyBox.Password = "";
                    AsrStatusText.Text = "Typed key is valid and was saved on this PC.";
                }
                else
                {
                    AsrStatusText.Text = ok
                        ? "Saved key is valid."
                        : (hasTyped ? "Typed key rejected (401). Check the key." : "Saved key rejected (401). Check the key.");
                }
            }
            catch (Exception ex)
            {
                AsrStatusText.Text = $"Test failed: {ex.Message}";
            }
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
        _shell?.Pipeline.SetHotkeyCaptureActive(false);
    }
}
#endif
