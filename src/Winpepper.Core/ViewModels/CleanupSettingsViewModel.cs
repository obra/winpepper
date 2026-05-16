using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Core.ViewModels;

public sealed class CleanupSettingsViewModel : INotifyPropertyChanged
{
    private readonly Action<CleanupSettingsContract> _persist;
    private CleanupSettingsContract _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CleanupSettingsViewModel(CleanupSettingsContract initial, Action<CleanupSettingsContract> persist)
    {
        _state = initial;
        _persist = persist;
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
