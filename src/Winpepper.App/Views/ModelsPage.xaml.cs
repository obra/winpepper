#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class ModelsPage : Page
{
    private bool _downloadInProgress;

    // Seeded from the cleanup gate in OnNavigatedTo (Task 5 wires it to
    // App.Shell.CleanupVm.Enabled); until then cleanup counts as enabled,
    // which matches today's behavior.
    private bool _cleanupEnabled = true;

    // True while a bottom-button run that includes the streaming model is
    // in flight, so the streaming state line can honestly say "Installing…".
    private bool _downloadRunIncludesStreaming;
    private bool _asrSelectedVerified;
    private CancellationTokenSource? _lifetimeCts;
    private EventHandler<StreamingAutoInstallStatus>? _autoInstallStatusChanged;

    public ModelsTabViewModel ViewModel { get; private set; } = null!;

    public ModelsPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var models = App.Shell!.ModelsServices;
        var settings = App.Shell!.SettingsStore;
        var s = settings.Load();
        _lifetimeCts = new CancellationTokenSource();

        ViewModel = new ModelsTabViewModel(
            models.Registry, models.ModelsRoot, models,
            currentAsrName: s.AsrModelName,
            currentCleanupName: s.CleanupModelName,
            promoteAsr: name =>
            {
                var shell = App.Shell!;
                shell.AsrModelSelection.Publish(name); // effective immediately
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { AsrModelName = name }); // durability
            },
            promoteCleanup: name =>
            {
                var shell = App.Shell!;
                shell.CleanupModelSelection.Publish(name); // effective immediately (next dictation)
                shell.CleanupVm.RefreshModelCapabilities(); // honesty note tracks the ACTIVE selection live
                shell.CleanupBackend.RequestPrewarm();     // background load so the next dictation doesn't pay it
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            },
            // The progress bridge requires an observable enqueue result: if
            // navigation/app shutdown has closed this queue, fail its drain
            // instead of waiting forever for a callback that cannot run.
            dispatch: a =>
            {
                if (!DispatcherQueue.TryEnqueue(() => a()))
                    throw new InvalidOperationException("The UI dispatcher rejected model progress work.");
            });

        AsrCombo.SelectedItem = ViewModel.AsrCard.SelectedDescriptor;
        CleanupCombo.SelectedItem = ViewModel.CleanupCard.SelectedDescriptor;
        StreamingCombo.SelectedItem = ViewModel.StreamingCard.SelectedDescriptor;
        // The background auto-install may finish (or fail) while this page is
        // open; refresh the streaming card's state line when it does.
        _autoInstallStatusChanged = (_, _) => DispatcherQueue.TryEnqueue(UpdateInstalledLabels);
        App.Shell!.StreamingAutoInstaller.StatusChanged += _autoInstallStatusChanged;
        UpdateInstalledLabels();
        WireSpeechProvider(s);
        try
        {
            var selectedAsr = App.Shell!.ModelsServices.Registry.ResolveOrDefault(
                App.Shell!.SettingsStore.Load().AsrModelName, ModelKind.Asr).Name;
            _asrSelectedVerified = await Task.Run(
                () => App.Shell!.ModelsServices.VerifyAsrModelReady(selectedAsr));
            UpdateInstalledLabels();
        }
        catch (OperationCanceledException)
        {
            // Navigation canceled the authoritative readiness refresh.
        }
        catch (Exception ex)
        {
            App.Shell!.LogFactory.CreateLogger<ModelsPage>()
                .LogError(ex, "ASR readiness verification failed");
            App.Shell!.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
            UpdateInstalledLabels();
        }
    }

    private async void OnAsrChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AsrCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.AsrCard.SelectedName = d.Name;
            ViewModel.AsrCard.CommitSelection();
            UpdateInstalledLabels();

            // Refresh the "Installed" state for the newly promoted model so the
            // label follows the live selection without renavigating.
            try
            {
                var selectedAsr = App.Shell!.ModelsServices.Registry
                    .ResolveOrDefault(d.Name, ModelKind.Asr).Name;
                _asrSelectedVerified = await Task.Run(
                    () => App.Shell!.ModelsServices.VerifyAsrModelReady(selectedAsr));
                UpdateInstalledLabels();
            }
            catch (Exception ex)
            {
                App.Shell!.LogFactory.CreateLogger<ModelsPage>()
                    .LogError(ex, "ASR readiness verification failed after promote");
                App.Shell!.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
                UpdateInstalledLabels();
            }
        }
    }

    private void OnCleanupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupCard.SelectedName = d.Name;
            ViewModel.CleanupCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    private void OnStreamingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StreamingCombo.SelectedItem is ModelDescriptor d)
        {
            // Streaming has no selection slot and no setting: the registry
            // pins the active streaming model, so the card's promote callback
            // is a deliberate no-op. The combo exists so future registry
            // entries appear automatically and feed the download button.
            ViewModel.StreamingCard.SelectedName = d.Name;
            ViewModel.StreamingCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    // All speech-recognition provider config lives here (owner decision: the
    // model section owns ASR config, including the API key). Ported verbatim
    // from the former RecordingPage "Speech recognition" section so behavior
    // (debounced settings writes, honest key testing, model canonicalization +
    // custom escape hatch) is unchanged.
    private void WireSpeechProvider(Winpepper.Core.Settings.AppSettings current)
    {
        var shell = App.Shell!;
        var keyStore = shell.AssemblyAiKeyStore;

        // Provider picker (radio buttons — owner preference over a dropdown)
        AsrProviderRadios.SelectedIndex =
            string.Equals(current.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AssemblyAiPanel.Visibility = AsrProviderRadios.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AsrProviderRadios.SelectionChanged += (_, _) =>
        {
            var tag = (AsrProviderRadios.SelectedItem as RadioButton)?.Tag as string ?? "local";
            AssemblyAiPanel.Visibility = tag == "assemblyai" ? Visibility.Visible : Visibility.Collapsed;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AsrProvider = tag });
        };

        // Model picker: known ids + an "Advanced/custom" escape hatch.
        const string CustomTag = "__custom__";
        AssemblyAiModelCombo.Items.Clear();
        foreach (var m in Winpepper.Asr.Transcription.AssemblyAiModels.Known)
            AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = "Advanced / custom\u2026", Tag = CustomTag });

        void SelectModelInCombo(string modelId)
        {
            // A model can be "known" via an accepted alias (e.g. "universal-3-pro")
            // that has no dedicated combo item; canonicalize first so any accepted
            // spelling resolves to the listed id, then look the item up safely. If no
            // listed item matches (truly custom id) we fall back to the custom item
            // rather than throwing.
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
            var tag = (AssemblyAiModelCombo.SelectedItem as ComboBoxItem)?.Tag as string
                      ?? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId;
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

        // Retention + keyterms toggles. Mutators execute at FLUSH time under
        // the mutator-replay writer (possibly on a threadpool thread if a
        // debounce tick races), and WinUI controls are thread-affine — so
        // capture IsOn into a local NOW, on the UI thread, and let the
        // lambda close over the local.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
        {
            var isOn = AssemblyAiDeleteToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = isOn });
        };
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
        {
            var isOn = AssemblyAiKeytermsToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = isOn });
        };

        // Streaming toggle (provider-agnostic; read LIVE per dictation by PipelineHost).
        StreamingToggle.IsOn = current.StreamingEnabled;
        StreamingToggle.Toggled += (_, _) =>
        {
            var isOn = StreamingToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { StreamingEnabled = isOn });
        };

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

            AsrStatusText.Text = hasTyped ? "Testing the key you typed\u2026" : "Testing the saved key\u2026";
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
    }

    private async void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        if (_downloadInProgress) return;

        var selection = CurrentSelection();
        var names = SelectedModelsPolicy.DownloadableMissingNames(selection);
        if (names.Count == 0) return; // button should already be disabled; belt-and-braces

        var shell = App.Shell!;
        var registry = shell.ModelsServices.Registry;
        var descriptors = names.Select(n => registry.Find(n)!).ToList();

        _downloadInProgress = true;
        _downloadRunIncludesStreaming =
            descriptors.Any(d => d.Kind == ModelKind.StreamingAsr);
        UpdateInstalledLabels(); // disables the button + shows "Installing…" where honest

        try
        {
            await ViewModel.DownloadSelectedAsync(descriptors, _lifetimeCts?.Token ?? CancellationToken.None);

            // Refresh the verified ASR flag off-thread so the label and the
            // button's enable state reflect the download that just finished
            // (previously the verify result was discarded and the label went
            // stale). This also primes ModelsServices' verified-readiness
            // cache so the synchronous check inside TryStart() below is a
            // cache hit, not a dispatcher-blocking re-hash.
            var canonicalAsr = registry
                .ResolveOrDefault(shell.AsrModelSelection.Read(), ModelKind.Asr).Name;
            _asrSelectedVerified = await Task.Run(() => shell.ModelsServices.VerifyAsrModelReady(canonicalAsr));

            // If the pipeline was left disabled at boot because models were
            // missing (issue #6), bring it up now that the download finished.
            shell.Pipeline.TryStart();
        }
        catch (OperationCanceledException)
        {
            // Navigation away cancels _lifetimeCts; cancellation must not
            // surface as an application crash.
        }
        catch (Exception ex)
        {
            shell.LogFactory.CreateLogger<ModelsPage>()
                .LogError(ex, "Model download failed");
            shell.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
        }
        finally
        {
            _downloadInProgress = false;
            _downloadRunIncludesStreaming = false;
            // Recompute rather than blindly re-enable: if everything the
            // dropdowns choose is now installed, the button must gray out.
            UpdateInstalledLabels();
        }
    }

    /// <summary>
    /// Snapshot of what the page's dropdowns currently choose, using the
    /// SAME installed-state sources the page already renders: the
    /// hash-verified flag for ASR, presence checks for cleanup/streaming.
    /// </summary>
    private IReadOnlyList<SelectedModelsPolicy.SelectedModel> CurrentSelection()
    {
        var models = App.Shell!.ModelsServices;

        SelectedModelsPolicy.SelectedModel? asr = ViewModel.AsrCard.SelectedDescriptor is { } a
            ? new(a.Name, _asrSelectedVerified, a.ManualInstallOnly) : null;
        SelectedModelsPolicy.SelectedModel? streaming = ViewModel.StreamingCard.SelectedDescriptor is { } s
            ? new(s.Name, s.IsFullyInstalledAndExtracted(models.ModelsRoot), s.ManualInstallOnly) : null;
        SelectedModelsPolicy.SelectedModel? cleanup = ViewModel.CleanupCard.SelectedDescriptor is { } c
            ? new(c.Name, ViewModel.CleanupCard.IsSelectedInstalled, c.ManualInstallOnly) : null;

        return SelectedModelsPolicy.BuildSelection(asr, streaming, cleanup, _cleanupEnabled);
    }

    private void UpdateDownloadButtonState()
    {
        var selection = CurrentSelection();

        // Disabled (grayed, not hidden) whenever its only effect is already
        // satisfied — and always while a run is in flight.
        DownloadSelectedButton.IsEnabled =
            !_downloadInProgress && SelectedModelsPolicy.DownloadButtonEnabled(selection);

        var manualNames = SelectedModelsPolicy.ManualOnlyMissingNames(selection);
        if (manualNames.Count > 0)
        {
            var registry = App.Shell!.ModelsServices.Registry;
            var displays = manualNames.Select(n => registry.Find(n)?.DisplayName ?? n);
            ManualInstallNote.Text =
                $"{string.Join(", ", displays)} must be installed manually — the download button can't fetch it.";
            ManualInstallNote.Visibility = Visibility.Visible;
        }
        else
        {
            ManualInstallNote.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateInstalledLabels()
    {
        var asrInstalled = _asrSelectedVerified;
        AsrInstalledText.Text = asrInstalled ? "Installed" : "Not downloaded";
        AsrInstalledIcon.Visibility = asrInstalled ? Visibility.Visible : Visibility.Collapsed;
        AsrNotInstalledIcon.Visibility = asrInstalled ? Visibility.Collapsed : Visibility.Visible;

        var cleanupInstalled = ViewModel.CleanupCard.IsSelectedInstalled;
        CleanupInstalledText.Text = cleanupInstalled ? "Installed" : "Not downloaded";
        CleanupInstalledIcon.Visibility = cleanupInstalled ? Visibility.Visible : Visibility.Collapsed;
        CleanupNotInstalledIcon.Visibility = cleanupInstalled ? Visibility.Collapsed : Visibility.Visible;

        var models = App.Shell!.ModelsServices;
        var streamingInstalled = ViewModel.StreamingCard.SelectedDescriptor
            ?.IsFullyInstalledAndExtracted(models.ModelsRoot) ?? false;
        // The background auto-install (AppShell.StartAsync) shares the page's
        // operation gate, so an Install click during it simply waits its turn
        // and then verify-short-circuits — but the state line must be honest
        // about what is happening right now.
        var autoStatus = App.Shell!.StreamingAutoInstaller.Status;
        var streamingBusy = (_downloadInProgress && _downloadRunIncludesStreaming)
            || autoStatus == StreamingAutoInstallStatus.Installing;
        StreamingInstalledText.Text = streamingInstalled ? "Installed"
            : streamingBusy ? "Installing\u2026"
            : autoStatus == StreamingAutoInstallStatus.Failed ? "Install failed \u2014 use the download button to retry"
            : "Not downloaded";
        StreamingInstalledIcon.Visibility = streamingInstalled ? Visibility.Visible : Visibility.Collapsed;
        StreamingNotInstalledIcon.Visibility = streamingInstalled ? Visibility.Collapsed : Visibility.Visible;

        UpdateDownloadButtonState();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_autoInstallStatusChanged is not null)
        {
            App.Shell!.StreamingAutoInstaller.StatusChanged -= _autoInstallStatusChanged;
            _autoInstallStatusChanged = null;
        }
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        base.OnNavigatedFrom(e);
    }
}
#endif
