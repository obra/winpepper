#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Winpepper.Platform.Hotkeys;

namespace Winpepper.App.Views.Controls;

public sealed partial class HotkeyRecorderBox : UserControl
{
    public event Action<string>? ChordRecorded;
    private bool _recording;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata("Hotkey",
            (d, e) => ((HotkeyRecorderBox)d).LabelBlock.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        IsTabStop = true;
    }

    public void SetChord(string chord, string? error)
    {
        ChordText.Text = chord;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _recording = true;
        ChordText.Text = "(press a chord)";
        Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;

        var mods = "";
        var window = Microsoft.UI.Xaml.Window.Current; // null in WinUI 3 — query via input mgr.
        var inputState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(e.Key);

        // Read modifier states with InputKeyboardSource (WinUI 3 API).
        bool IsDown(VirtualKey vk) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(vk) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (IsDown(VirtualKey.LeftControl))  mods += "LeftCtrl+";
        if (IsDown(VirtualKey.RightControl)) mods += "RightCtrl+";
        if (IsDown(VirtualKey.LeftShift))    mods += "LeftShift+";
        if (IsDown(VirtualKey.RightShift))   mods += "RightShift+";
        if (IsDown(VirtualKey.LeftMenu))     mods += "LeftAlt+";
        if (IsDown(VirtualKey.RightMenu))    mods += "RightAlt+";
        if (IsDown(VirtualKey.LeftWindows))  mods += "LeftWin+";
        if (IsDown(VirtualKey.RightWindows)) mods += "RightWin+";

        var keyName = KeyToName(e.Key);
        if (keyName is null) return;

        var chord = mods + keyName;
        try
        {
            HotkeyChord.Parse(chord);
            SetChord(chord, null);
            ChordRecorded?.Invoke(chord);
            _recording = false;
            e.Handled = true;
        }
        catch
        {
            SetChord("(invalid)", "Could not parse that combination.");
        }
    }

    private static string? KeyToName(VirtualKey k) => k switch
    {
        VirtualKey.Space  => "Space",
        VirtualKey.Tab    => "Tab",
        VirtualKey.Enter  => "Enter",
        VirtualKey.Escape => "Esc",
        >= VirtualKey.A and <= VirtualKey.Z => k.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)k - (int)VirtualKey.Number0).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => $"F{(int)k - (int)VirtualKey.F1 + 1}",
        _ => null,
    };
}
#endif
