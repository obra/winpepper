using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public sealed class OnboardingViewModel : INotifyPropertyChanged
{
    private readonly ISettingsWriter _writer;
    private readonly Func<Task> _runDownloader;
    private readonly IHotkeyValidator _validator;

    private OnboardingStep _step = OnboardingStep.PickMic;
    private string _micId = "";
    private string _holdHotkey = "RightCtrl+RightShift";
    private string _toggleHotkey = "Ctrl+Shift+Space";
    private bool _testDictationDone;
    private readonly Action<Exception>? _onDownloadError;
    private string? _downloadError;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Spec §7.4 requires real hotkey-conflict detection during onboarding.
    /// <paramref name="validator"/> is required — production wires a
    /// <c>PlatformHotkeyValidator</c>; unit tests pass a fake. There is no
    /// permissive default: a permissive default would mask conflicts on the
    /// onboarding step.
    /// </summary>
    public OnboardingViewModel(ISettingsWriter writer, Func<Task> runDownloader, IHotkeyValidator validator,
                               Action<Exception>? onDownloadError = null)
    {
        _writer = writer;
        _runDownloader = runDownloader;
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _onDownloadError = onDownloadError;
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

    public string? HoldHotkeyError => Validate(_holdHotkey, _toggleHotkey, isToggle: false);
    public string? ToggleHotkeyError => Validate(_toggleHotkey, _holdHotkey, isToggle: true);

    public bool CanAdvance => _step switch
    {
        OnboardingStep.PickMic        => !string.IsNullOrEmpty(_micId),
        OnboardingStep.PickHotkeys    => HoldHotkeyError is null && ToggleHotkeyError is null,
        OnboardingStep.DownloadModels => true,
        OnboardingStep.TestDictation  => _testDictationDone,
        _ => false,
    };

    public bool CanSkip => _step == OnboardingStep.DownloadModels;

    /// <summary>
    /// Friendly, inline error message shown on the Download step when the model
    /// download fails. Null when there is no error. The Download button doubles
    /// as Retry: a fresh AdvanceAsync clears this before trying again.
    /// </summary>
    public string? DownloadError
    {
        get => _downloadError;
        private set
        {
            if (_downloadError == value) return;
            _downloadError = value;
            Raise();
            Raise(nameof(HasDownloadError));
        }
    }

    public bool HasDownloadError => _downloadError is not null;

    public async Task AdvanceAsync()
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
                DownloadError = null;
                try
                {
                    await _runDownloader();
                }
                catch (Exception ex)
                {
                    // Never let a network/download failure crash the wizard.
                    // Stay on this step so Retry (the Download button) and Skip
                    // remain usable; surface a friendly inline message.
                    _onDownloadError?.Invoke(ex);
                    DownloadError = "Couldn't download the models. Check your connection and try again, or Skip to set them up later.";
                    return;
                }
                Step = OnboardingStep.TestDictation;
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
    public async Task SkipAsync()
    {
        if (!CanSkip) return;
        await _writer.QueueAndFlushAsync(s => s with { OnboardingCompleted = true });
        Step = OnboardingStep.TestDictation;
    }

    private string? Validate(string chord, string other, bool isToggle)
    {
        var sys = _validator.Validate(chord);
        if (sys is not null) return sys;
        if (_validator.Clash(chord, other))
            return isToggle ? "Same as Hold." : "Same as Toggle.";
        return null;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
