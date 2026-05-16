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

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Spec §7.4 requires real hotkey-conflict detection during onboarding.
    /// <paramref name="validator"/> is required — production wires a
    /// <c>PlatformHotkeyValidator</c>; unit tests pass a fake. There is no
    /// permissive default: a permissive default would mask conflicts on the
    /// onboarding step.
    /// </summary>
    public OnboardingViewModel(ISettingsWriter writer, Func<Task> runDownloader, IHotkeyValidator validator)
    {
        _writer = writer;
        _runDownloader = runDownloader;
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
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

    public async Task AdvanceAsync()
    {
        if (!CanAdvance) return;
        switch (_step)
        {
            case OnboardingStep.PickMic:
                _writer.Queue(s => s with { MicDeviceId = _micId });
                Step = OnboardingStep.PickHotkeys;
                break;
            case OnboardingStep.PickHotkeys:
                _writer.Queue(s => s with { HoldHotkey = _holdHotkey, ToggleHotkey = _toggleHotkey });
                Step = OnboardingStep.DownloadModels;
                break;
            case OnboardingStep.DownloadModels:
                await _runDownloader();
                Step = OnboardingStep.TestDictation;
                break;
            case OnboardingStep.TestDictation:
                _writer.Queue(s => s with { OnboardingCompleted = true });
                await _writer.FlushAsync();
                Step = OnboardingStep.Done;
                break;
        }
    }

    public void Skip()
    {
        if (!CanSkip) return;
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
