using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Core.ViewModels;

public sealed class CleanupSettingsViewModel : INotifyPropertyChanged
{
    private readonly Action<CleanupSettingsContract> _persist;
    private CleanupSettingsContract _state;

    /// <summary>Pull delegate wired in AppShell: does the ACTIVE cleanup model's
    /// prompt format carry system-prompt content (profile, custom prompt, window
    /// context)? Core has no project references, so the capability arrives as a
    /// delegate over PromptFormatCapabilities + the selection slot. Null (tests,
    /// legacy callers) means "supported".</summary>
    private readonly Func<bool>? _promptSettingsSupported;
    private bool _promptSettingsSupportedValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CleanupSettingsViewModel(
        CleanupSettingsContract initial,
        Action<CleanupSettingsContract> persist,
        Func<bool>? promptSettingsSupported = null)
    {
        _state = initial;
        _persist = persist;
        _promptSettingsSupported = promptSettingsSupported;
        _promptSettingsSupportedValue = promptSettingsSupported?.Invoke() ?? true;
    }

    /// <summary>False while the active cleanup model ignores in-prompt steering
    /// (raw-io). The page grays out Profile/CustomPrompt/WindowContext and shows
    /// the honesty note. Stored values are never touched.</summary>
    public bool PromptSettingsSupported => _promptSettingsSupportedValue;

    /// <summary>Re-read the capability delegate; called on page entry and from
    /// the Models-page promote callback so the note updates live.</summary>
    public void RefreshModelCapabilities()
    {
        var next = _promptSettingsSupported?.Invoke() ?? true;
        if (next == _promptSettingsSupportedValue) return;
        _promptSettingsSupportedValue = next;
        Raise(nameof(PromptSettingsSupported));
    }

    public bool Enabled
    {
        get => _state.Enabled;
        set => Apply(_state with { Enabled = value }, nameof(Enabled));
    }

    public bool WindowContextEnabled
    {
        get => _state.WindowContextEnabled;
        set => Apply(_state with { WindowContextEnabled = value }, nameof(WindowContextEnabled));
    }

    public string Profile
    {
        get => _state.Profile;
        set
        {
            Apply(_state with { Profile = value }, nameof(Profile));
            Raise(nameof(CustomPromptEditable));
        }
    }

    public string CustomPrompt
    {
        get => _state.CustomPrompt;
        set => Apply(_state with { CustomPrompt = value }, nameof(CustomPrompt));
    }

    public int MaxNewTokens
    {
        get => _state.MaxNewTokens;
        set
        {
            var clamped = Math.Clamp(value, 64, 4096);
            if (clamped == _state.MaxNewTokens) return;
            Apply(_state with { MaxNewTokens = clamped }, nameof(MaxNewTokens));
        }
    }

    public int TimeoutMs
    {
        get => _state.TimeoutMs;
        set
        {
            var clamped = Math.Clamp(value, 2000, 60000);
            if (clamped == _state.TimeoutMs) return;
            Apply(_state with { TimeoutMs = clamped }, nameof(TimeoutMs));
        }
    }

    public bool CustomPromptEditable => string.Equals(_state.Profile, "Custom", StringComparison.Ordinal);

    private void Apply(CleanupSettingsContract next, string property)
    {
        if (Equals(next, _state)) return;
        _state = next;
        _persist(next);
        Raise(property);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
