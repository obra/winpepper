using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public interface IHotkeyValidator
{
    /// <summary>Returns null when valid; an error or conflict description otherwise.</summary>
    string? Validate(string chord, bool allowLongPressSpace = false);
    /// <summary>Returns true when the two chords would fire on the same key event.</summary>
    bool Clash(string a, string b);
}

public sealed class RecordingSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsWriter _writer;
    private readonly IHotkeyValidator _validator;
    private string _holdHotkey;
    private string _toggleHotkey;
    private string _micDeviceId;
    private bool _playSounds;
    private bool _speakerFilterEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecordingSettingsViewModel(AppSettings initial, ISettingsWriter writer, IHotkeyValidator? validator = null)
    {
        _writer = writer;
        _validator = validator ?? new NullHotkeyValidator();
        _holdHotkey = initial.HoldHotkey;
        _toggleHotkey = initial.ToggleHotkey;
        _micDeviceId = initial.MicDeviceId;
        _playSounds = initial.PlaySounds;
        _speakerFilterEnabled = initial.SpeakerFilterEnabled;
    }

    private sealed class NullHotkeyValidator : IHotkeyValidator
    {
        public string? Validate(string chord, bool allowLongPressSpace = false) => null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    public string HoldHotkey
    {
        get => _holdHotkey;
        set
        {
            if (_holdHotkey == value) return;
            _holdHotkey = value;
            _writer.Queue(s => s with { HoldHotkey = value });
            Raise(nameof(HoldHotkey));
            Raise(nameof(HoldHotkeyConflict));
            Raise(nameof(ToggleHotkeyConflict));
        }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set
        {
            if (_toggleHotkey == value) return;
            _toggleHotkey = value;
            _writer.Queue(s => s with { ToggleHotkey = value });
            Raise(nameof(ToggleHotkey));
            Raise(nameof(HoldHotkeyConflict));
            Raise(nameof(ToggleHotkeyConflict));
        }
    }

    public string MicDeviceId
    {
        get => _micDeviceId;
        set
        {
            if (_micDeviceId == value) return;
            _micDeviceId = value;
            _writer.Queue(s => s with { MicDeviceId = value });
            Raise(nameof(MicDeviceId));
        }
    }

    public bool PlaySounds
    {
        get => _playSounds;
        set
        {
            if (_playSounds == value) return;
            _playSounds = value;
            _writer.Queue(s => s with { PlaySounds = value });
            Raise(nameof(PlaySounds));
        }
    }

    public bool SpeakerFilterEnabled
    {
        get => _speakerFilterEnabled;
        set
        {
            if (_speakerFilterEnabled == value) return;
            _speakerFilterEnabled = value;
            _writer.Queue(s => s with { SpeakerFilterEnabled = value });
            Raise(nameof(SpeakerFilterEnabled));
        }
    }

    public string? HoldHotkeyConflict => DescribeChord(_holdHotkey, _toggleHotkey, isToggle: false);
    public string? ToggleHotkeyConflict => DescribeChord(_toggleHotkey, _holdHotkey, isToggle: true);

    private string? DescribeChord(string chord, string other, bool isToggle)
    {
        var sys = _validator.Validate(chord, allowLongPressSpace: !isToggle);
        if (sys is not null) return sys;
        if (_validator.Clash(chord, other))
            return isToggle ? "Same as Hold hotkey." : "Same as Toggle hotkey.";
        return null;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
