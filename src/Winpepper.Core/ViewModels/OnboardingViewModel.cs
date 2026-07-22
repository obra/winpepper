using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public sealed class OnboardingViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsWriter _writer;
    private readonly IAsrProvisioningService _provisioner;
    private readonly Func<bool> _tryStartPipeline;
    private readonly IHotkeyValidator _validator;

    private OnboardingStep _step = OnboardingStep.PickMic;
    private string _micId = "";
    private string _holdHotkey = "RightCtrl+RightShift";
    private string _toggleHotkey = "Ctrl+Shift+Space";
    private bool _testDictationDone;
    private bool _isBusy;
    private double _downloadProgressPercent;
    private string _downloadStatus = "Speech model required";
    private string? _downloadError;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Spec §7.4 requires real hotkey-conflict detection during onboarding.
    /// <paramref name="validator"/> is required — production wires a
    /// <c>PlatformHotkeyValidator</c>; unit tests pass a fake. There is no
    /// permissive default: a permissive default would mask conflicts on the
    /// onboarding step.
    /// </summary>
    public OnboardingViewModel(
        ISettingsWriter writer,
        IAsrProvisioningService provisioner,
        Func<bool> tryStartPipeline,
        IHotkeyValidator validator)
    {
        _writer = writer;
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _provisioner.StateChanged += OnProvisioningStateChanged;
        ApplyProvisioningState(_provisioner.State);
    }

    /// <summary>
    /// Prefill the VM from persisted settings and start at the first unresolved
    /// step. All steps remain reachable via Back; this only moves the starting
    /// position (spec 3(i),(ii)). <paramref name="persistedMicPresent"/> is
    /// supplied by the page (device enumeration lives there);
    /// <paramref name="modelsResolved"/> is true when no selected model is
    /// missing (computed via MissingModelsResolver).
    /// </summary>
    public void InitializeFrom(AppSettings settings, bool persistedMicPresent, bool modelsResolved)
    {
        _micId = settings.MicDeviceId;
        _holdHotkey = settings.HoldHotkey;
        _toggleHotkey = settings.ToggleHotkey;

        Raise(nameof(SelectedMicDeviceId));
        Raise(nameof(HoldHotkey));
        Raise(nameof(ToggleHotkey));
        Raise(nameof(HoldHotkeyError));
        Raise(nameof(ToggleHotkeyError));

        Step = FirstUnresolvedStep(persistedMicPresent, modelsResolved);
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
        private set { if (_step == value) return; _step = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(CanSkip)); }
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            Raise();
            Raise(nameof(CanAdvance));
            Raise(nameof(CanRetry));
        }
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
        OnboardingStep.DownloadModels => !_isBusy,
        OnboardingStep.TestDictation  => _testDictationDone,
        _ => false,
    };

    public bool CanSkip => false;
    public bool CanRetry => _step == OnboardingStep.DownloadModels && !_isBusy && _downloadError is not null;

    /// <summary>
    /// Convenience flag for the onboarding page: true when an inline download
    /// error should be shown on the Download step. Backed by the same
    /// <see cref="DownloadError"/> the provisioner surfaces.
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
                await ProvisionAndStartPipelineAsync(ct);
                break;
            case OnboardingStep.TestDictation:
                await _writer.QueueAndFlushAsync(s => s with { OnboardingCompleted = true });
                Step = OnboardingStep.Done;
                break;
        }
    }

    /// <summary>
    /// Skipping the (optional) model download still completes setup: the user
    /// chose to skip, so persist OnboardingCompleted durably and move on to the
    /// test-dictation step. This prevents onboarding from reappearing forever
    /// (spec 2(iii)).
    /// </summary>
    public void Skip()
    {
        // Model skipping is deliberately unavailable: Test Dictation requires
        // verified ASR files and a pipeline that has successfully started.
    }

    private async Task ProvisionAndStartPipelineAsync(CancellationToken ct)
    {
        IsBusy = true;
        DownloadError = null;
        try
        {
            await _provisioner.EnsureReadyAsync(ct);
            if (!await _provisioner.VerifyReadyAsync(ct))
            {
                DownloadError = "The speech model could not be verified. Retry the download.";
                return;
            }

            if (!_tryStartPipeline())
            {
                DownloadError = "The dictation pipeline could not start. Retry after checking the speech model.";
                return;
            }

            Step = OnboardingStep.TestDictation;
        }
        catch (OperationCanceledException)
        {
            DownloadError = "Speech model download was canceled. Retry to resume.";
        }
        catch (Exception ex)
        {
            DownloadError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnProvisioningStateChanged(object? sender, AsrProvisioningState state)
        => ApplyProvisioningState(state);

    private void ApplyProvisioningState(AsrProvisioningState state)
    {
        DownloadProgressPercent = Math.Clamp(state.ProgressPercent, 0, 100);
        DownloadStatus = state.Status switch
        {
            AsrProvisioningStatus.Missing => "Speech model required",
            AsrProvisioningStatus.Downloading => "Downloading speech model",
            AsrProvisioningStatus.Verifying => "Verifying speech model",
            AsrProvisioningStatus.Retrying => "Retrying speech model download",
            AsrProvisioningStatus.Ready => "Speech model ready",
            AsrProvisioningStatus.Failed => "Speech model download failed",
            _ => "Preparing speech model",
        };
        if (state.ErrorMessage is not null) DownloadError = state.ErrorMessage;
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

    public void Dispose() => _provisioner.StateChanged -= OnProvisioningStateChanged;
}
