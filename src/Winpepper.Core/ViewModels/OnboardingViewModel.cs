using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public sealed class OnboardingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsWriter _writer;
    private readonly Func<bool> _tryStartPipeline;
    private readonly IHotkeyValidator _validator;
    private readonly IOnboardingModelProvisioner _modelProvisioner;
    private readonly ModelPickerCatalog _catalog;

    private OnboardingStep _step = OnboardingStep.PickMic;
    private string _micId = "";
    private string _holdHotkey = "RightCtrl+RightShift";
    private string _toggleHotkey = "Ctrl+Shift+Space";
    private bool _testDictationDone;
    private bool _multilingualSelected;
    private bool _backupModelSelected;
    private bool _cleanupModelSelected;
    private bool _speechModelVerified;
    private bool _pipelineStartAttempted;
    private double _downloadProgressPercent;
    private string _downloadStatus = "Speech model required";
    private string? _downloadError;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Spec §7.4 requires real hotkey-conflict detection during onboarding.
    /// <paramref name="validator"/> is required — production wires a
    /// <c>PlatformHotkeyValidator</c>; unit tests pass a fake. There is no
    /// permissive default: a permissive default would mask conflicts on the
    /// onboarding step. <paramref name="catalog"/> supplies registry facts
    /// (names + bytes) as plain data — Core has no Models reference by design.
    /// </summary>
    public OnboardingViewModel(
        ISettingsWriter writer,
        Func<bool> tryStartPipeline,
        IHotkeyValidator validator,
        IOnboardingModelProvisioner modelProvisioner,
        ModelPickerCatalog catalog)
    {
        _writer = writer;
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _modelProvisioner = modelProvisioner ?? throw new ArgumentNullException(nameof(modelProvisioner));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _modelProvisioner.StateChanged += OnDownloadStateChanged;
    }

    /// <summary>
    /// Prefill the VM from persisted settings and start at the first unresolved
    /// step (spec 3(i),(ii)). Model-picker choices are rehydrated from settings.
    /// When the resolved starting step is TestDictation, downloads are
    /// re-issued with the persisted selection so speech-model verification
    /// re-runs and any interrupted optional downloads resume.
    /// <paramref name="persistedMicPresent"/> is supplied by the page (device
    /// enumeration lives there); <paramref name="modelsResolved"/> is true when
    /// no selected model is missing (computed via MissingModelsResolver).
    /// </summary>
    public void InitializeFrom(AppSettings settings, bool persistedMicPresent, bool modelsResolved)
    {
        _micId = settings.MicDeviceId;
        _holdHotkey = settings.HoldHotkey;
        _toggleHotkey = settings.ToggleHotkey;
        _multilingualSelected = settings.StreamingModelName == _catalog.MultilingualName;
        _backupModelSelected = settings.OnboardingBackupModelChosen;
        _cleanupModelSelected = settings.OnboardingCleanupModelChosen;

        Raise(nameof(SelectedMicDeviceId));
        Raise(nameof(HoldHotkey));
        Raise(nameof(ToggleHotkey));
        Raise(nameof(HoldHotkeyError));
        Raise(nameof(ToggleHotkeyError));
        Raise(nameof(MultilingualSelected));
        Raise(nameof(BackupModelSelected));
        Raise(nameof(CleanupModelSelected));
        Raise(nameof(TotalDownloadText));

        Step = FirstUnresolvedStep(persistedMicPresent, modelsResolved);

        if (Step == OnboardingStep.TestDictation)
            _modelProvisioner.StartDownloads(BuildDownloadNames(), SelectedSpeechModelName);
    }

    private OnboardingStep FirstUnresolvedStep(bool persistedMicPresent, bool modelsResolved)
    {
        var micResolved = !string.IsNullOrEmpty(_micId) && persistedMicPresent;
        if (!micResolved) return OnboardingStep.PickMic;

        var hotkeysResolved = HoldHotkeyError is null && ToggleHotkeyError is null;
        if (!hotkeysResolved) return OnboardingStep.PickHotkeys;

        if (!modelsResolved) return OnboardingStep.DownloadModels;

        return OnboardingStep.TestDictation;
    }

    public OnboardingStep Step
    {
        get => _step;
        private set { if (_step == value) return; _step = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(CanSkip)); Raise(nameof(CanRetry)); }
    }

    public string SelectedMicDeviceId
    {
        get => _micId;
        set { if (_micId == value) return; _micId = value; Raise(); Raise(nameof(CanAdvance)); }
    }

    public string HoldHotkey
    {
        get => _holdHotkey;
        set { if (_holdHotkey == value) return; _holdHotkey = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(HoldHotkeyError)); Raise(nameof(ToggleHotkeyError)); }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set { if (_toggleHotkey == value) return; _toggleHotkey = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(HoldHotkeyError)); Raise(nameof(ToggleHotkeyError)); }
    }

    public bool TestDictationDone
    {
        get => _testDictationDone;
        set { if (_testDictationDone == value) return; _testDictationDone = value; Raise(); Raise(nameof(CanAdvance)); }
    }

    public bool MultilingualSelected
    {
        get => _multilingualSelected;
        set { if (_multilingualSelected == value) return; _multilingualSelected = value; Raise(); Raise(nameof(TotalDownloadText)); }
    }

    public bool BackupModelSelected
    {
        get => _backupModelSelected;
        set { if (_backupModelSelected == value) return; _backupModelSelected = value; Raise(); Raise(nameof(TotalDownloadText)); }
    }

    public bool CleanupModelSelected
    {
        get => _cleanupModelSelected;
        set { if (_cleanupModelSelected == value) return; _cleanupModelSelected = value; Raise(); Raise(nameof(TotalDownloadText)); }
    }

    public string SelectedSpeechModelName => _multilingualSelected ? _catalog.MultilingualName : _catalog.EnglishName;

    public string TotalDownloadText => "Total download: " + FormatTotal(TotalMb());

    /// <summary>Card size label, e.g. "~760 MB" (MB rounded to the nearest 10).</summary>
    public static string SizeLabel(long bytes)
    {
        var mb = (int)Math.Round(bytes / 1_000_000.0);
        var rounded = (int)(Math.Round(mb / 10.0) * 10);
        return $"~{rounded} MB";
    }

    private int TotalMb()
    {
        // Per-item MB uses the SAME rounding as SizeLabel's first step, then sums.
        var totalMb = Mb(_multilingualSelected ? _catalog.MultilingualBytes : _catalog.EnglishBytes);
        if (_backupModelSelected) totalMb += Mb(_catalog.BackupBytes);
        if (_cleanupModelSelected) totalMb += Mb(_catalog.CleanupBytes);
        return totalMb;
    }

    private static int Mb(long bytes) => (int)Math.Round(bytes / 1_000_000.0);

    private static string FormatTotal(int totalMb)
        => totalMb >= 1000 ? $"{totalMb / 1000.0:0.0} GB" : $"{totalMb} MB";

    /// <summary>
    /// True once the speech model's files verified AND the dictation pipeline
    /// started successfully — the Test-Dictation finish gate.
    /// </summary>
    public bool SpeechModelVerified
    {
        get => _speechModelVerified;
        private set { if (_speechModelVerified == value) return; _speechModelVerified = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(CanRetry)); }
    }

    public double DownloadProgressPercent
    {
        get => _downloadProgressPercent;
        private set { if (_downloadProgressPercent == value) return; _downloadProgressPercent = value; Raise(); }
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set { if (_downloadStatus == value) return; _downloadStatus = value; Raise(); }
    }

    public string? DownloadError
    {
        get => _downloadError;
        private set
        {
            if (_downloadError == value) return;
            _downloadError = value;
            Raise();
            Raise(nameof(CanRetry));
            Raise(nameof(HasDownloadError));
        }
    }

    public string? HoldHotkeyError => Validate(_holdHotkey, _toggleHotkey, isToggle: false);
    public string? ToggleHotkeyError => Validate(_toggleHotkey, _holdHotkey, isToggle: true);

    public bool CanAdvance => _step switch
    {
        OnboardingStep.PickMic        => !string.IsNullOrEmpty(_micId),
        OnboardingStep.PickHotkeys    => HoldHotkeyError is null && ToggleHotkeyError is null,
        OnboardingStep.DownloadModels => true, // click always allowed; downloads run in the background
        OnboardingStep.TestDictation  => _testDictationDone && _speechModelVerified,
        _ => false,
    };

    public bool CanSkip => false;
    public bool CanRetry => _step == OnboardingStep.TestDictation && !_speechModelVerified && _downloadError is not null;

    /// <summary>
    /// Convenience flag for the onboarding page: true when an inline download
    /// error should be shown. Backed by the same <see cref="DownloadError"/>
    /// the provisioner surfaces.
    /// </summary>
    public bool HasDownloadError => _downloadError is not null;

    public Task AdvanceAsync() => AdvanceAsync(CancellationToken.None);

    public async Task AdvanceAsync(CancellationToken ct)
    {
        if (!CanAdvance) return;
        switch (_step)
        {
            case OnboardingStep.PickMic:
                await _writer.QueueAndFlushAsync(s => s with { MicDeviceId = _micId });
                Step = OnboardingStep.PickHotkeys;
                break;
            case OnboardingStep.PickHotkeys:
                await _writer.QueueAndFlushAsync(s => s with { HoldHotkey = _holdHotkey, ToggleHotkey = _toggleHotkey });
                Step = OnboardingStep.DownloadModels;
                break;
            case OnboardingStep.DownloadModels:
                await _writer.QueueAndFlushAsync(s => s with
                {
                    StreamingModelName = SelectedSpeechModelName,
                    OnboardingBackupModelChosen = _backupModelSelected,
                    OnboardingCleanupModelChosen = _cleanupModelSelected,
                    // Opting into the cleanup MODEL here is the user's opt-in
                    // gesture for the feature itself (cleanup is off by default
                    // since 2026-08-24). Only ever turns it ON — an earlier
                    // explicit choice is never stripped.
                    CleanupEnabled = _cleanupModelSelected ? true : s.CleanupEnabled,
                    // Same for the backup ASR (2026-08-25): its active-name
                    // default is None, so choosing the backup MODEL must point
                    // AsrModelName at it or the user downloads files that never
                    // run. Only ever sets it; a declined backup leaves prior
                    // choices intact.
                    AsrModelName = _backupModelSelected ? _catalog.BackupName : s.AsrModelName,
                });
                _modelProvisioner.StartDownloads(BuildDownloadNames(), SelectedSpeechModelName);
                Step = OnboardingStep.TestDictation; // advance immediately; downloads continue in the background
                break;
            case OnboardingStep.TestDictation:
                await _writer.QueueAndFlushAsync(s => s with { OnboardingCompleted = true });
                Step = OnboardingStep.Done;
                break;
        }
    }

    public void Skip()
    {
        // Skipping is deliberately unavailable: Test Dictation requires a
        // verified speech model and a pipeline that has successfully started.
    }

    /// <summary>
    /// Re-issues the background downloads with the persisted selection (the
    /// provisioner treats a post-failure call as a retry) and re-arms the
    /// one-shot pipeline-start attempt.
    /// </summary>
    public void RetryDownloads()
    {
        _pipelineStartAttempted = false;
        DownloadError = null;
        _modelProvisioner.StartDownloads(BuildDownloadNames(), SelectedSpeechModelName);
    }

    private IReadOnlyList<string> BuildDownloadNames()
    {
        var names = new List<string> { SelectedSpeechModelName };
        if (_backupModelSelected) names.Add(_catalog.BackupName);
        if (_cleanupModelSelected) names.Add(_catalog.CleanupName);
        return names;
    }

    private void OnDownloadStateChanged(object? sender, OnboardingDownloadState state)
    {
        DownloadProgressPercent = state.ProgressPercent;
        DownloadStatus = state.StatusText;
        if (state.Error is not null) DownloadError = state.Error;

        // One pipeline-start attempt per SpeechModelReady rising edge;
        // RetryDownloads re-arms it.
        if (state.SpeechModelReady && !_pipelineStartAttempted)
        {
            _pipelineStartAttempted = true;
            SpeechModelVerified = _tryStartPipeline();
            if (!SpeechModelVerified)
                DownloadError = "The dictation pipeline could not start. Retry after checking the speech model.";
        }
    }

    private string? Validate(string chord, string other, bool isToggle)
    {
        var sys = _validator.Validate(chord, allowLongPressSpace: !isToggle);
        if (sys is not null) return sys;
        if (_validator.Clash(chord, other))
            return isToggle ? "Same as Hold." : "Same as Toggle.";
        return null;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _modelProvisioner.StateChanged -= OnDownloadStateChanged;
}
