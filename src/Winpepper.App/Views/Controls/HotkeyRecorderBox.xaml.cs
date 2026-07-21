#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Winpepper.Platform.Hotkeys;

namespace Winpepper.App.Views.Controls;

public sealed partial class HotkeyRecorderBox : UserControl
{
    public event Action<string>? ChordRecorded;
    public event Action<bool>? RecordingStateChanged;

    private readonly ChordRecorder _recorder = new();
    private string _chordBeforeRecording = "";
    private ILogger? _logCache;
    private ILogger? Log => _logCache ??= App.Shell?.LogFactory.CreateLogger("HotkeyRecorderBox");

    // Guarantees the global hook is un-suspended if this control is torn down
    // mid-recording (window close / page unload) without Cancel/Commit/LostFocus.
    private readonly RecorderSuspendCoordinator _suspend;

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
        _suspend = new RecorderSuspendCoordinator(recording => RecordingStateChanged?.Invoke(recording));
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        LostFocus += OnLostFocus;
        Unloaded += OnUnloaded;
        IsTabStop = true;
    }

    // Torn down (page navigated away, window closed) - release suspend and stop
    // any in-flight recording so global hotkeys can never be left dead.
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _recorder.Cancel();
        _suspend.Teardown();
    }

    public void SetChord(string chord, string? error)
    {
        ChordText.Text = chord;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _chordBeforeRecording = ChordText.Text;
        _recorder.Begin();
        _suspend.SetRecording(true);
        ChordText.Text = "(press a chord - Esc cancels)";
        RecordButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        Log?.LogInformation("Hotkey recording started ({Label})", Label);
        Focus(FocusState.Programmatic);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRecording("cancel button");

    // Clicking anywhere else (nav rail, another control) moves focus off this
    // control; treat that as a cancel instead of leaving the recording armed
    // (issue #11). LostFocus bubbles from the inner buttons too, so only act
    // when it is the control itself that lost focus.
    private void OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, this)) return;
        CancelRecording("focus lost");
    }

    private void CancelRecording(string reason)
    {
        if (!_recorder.Cancel()) return;
        _suspend.SetRecording(false);
        ChordText.Text = _chordBeforeRecording;
        ResetButtons();
        Log?.LogInformation("Hotkey recording cancelled ({Label}): {Reason}", Label, reason);
    }

    private void ResetButtons()
    {
        RecordButton.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recorder.IsRecording) return;

        if (IsModifierKey(e.Key))
        {
            if (!TryGetCurrentModifierPrefix(e, out var heldModifiers)) return;
            HandleRecorderResult(_recorder.OnModifierKeyDown(heldModifiers), e);
            return;
        }

        if (!TryGetCurrentModifierPrefix(e, out var mods)) return;
        HandleRecorderResult(_recorder.OnKey(KeyToName(e.Key), mods, e.Key == VirtualKey.Escape), e);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recorder.IsRecording || !IsModifierKey(e.Key)) return;

        // Modifier-only chords are complete when the user begins releasing
        // them. The recorder retained the largest modifier set observed on
        // key-down, before InputKeyboardSource dropped the released key.
        HandleRecorderResult(_recorder.OnModifierKeyUp(), e);
    }

    private bool TryGetCurrentModifierPrefix(KeyRoutedEventArgs e, out string modifiers)
    {
        try
        {
            modifiers = CurrentModifierPrefix();
            return true;
        }
        catch (Exception ex)
        {
            modifiers = "";
            // InputKeyboardSource reads keyboard state through COM and can
            // fail (E_ACCESSDENIED) while focus is moving between windows
            // (issue #11). Cancel cleanly and surface the error instead of
            // letting the exception take down the process.
            Log?.LogError(ex, "Hotkey recording failed reading modifier state ({Label})", Label);
            App.Shell?.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Hotkey, ex, Guid.Empty);
            CancelRecording("modifier state unavailable");
            SetChord(_chordBeforeRecording, "Couldn't read the keyboard state. Try recording again.");
            e.Handled = true;
            return false;
        }
    }

    private void HandleRecorderResult(ChordKeyResult result, KeyRoutedEventArgs e)
    {
        switch (result)
        {
            case ChordKeyResult.Cancelled:
                _suspend.SetRecording(false);
                ChordText.Text = _chordBeforeRecording;
                ResetButtons();
                Log?.LogInformation("Hotkey recording cancelled ({Label}): Esc", Label);
                e.Handled = true;
                break;
            case ChordKeyResult.Committed:
                CommitRecordedChord();
                e.Handled = true;
                break;
            case ChordKeyResult.Invalid:
                SetChord("(invalid)", "Could not parse that combination.");
                break;
            // Ignored: unmapped key — keep waiting.
        }
    }

    private void CommitRecordedChord()
    {
        var chord = _recorder.CommittedChord!;
        SetChord(chord, null);
        ResetButtons();
        ChordRecorded?.Invoke(chord);
        _suspend.SetRecording(false);
        Log?.LogInformation("Hotkey recording committed ({Label}): {Chord}", Label, chord);
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or
        VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    // Reads modifier states with InputKeyboardSource (WinUI 3 API). COM-backed;
    // callers must be ready for it to throw (see TryGetCurrentModifierPrefix).
    private static string CurrentModifierPrefix()
    {
        bool IsDown(VirtualKey vk) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(vk) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        var mods = "";
        if (IsDown(VirtualKey.LeftControl))  mods += "LeftCtrl+";
        if (IsDown(VirtualKey.RightControl)) mods += "RightCtrl+";
        if (IsDown(VirtualKey.LeftShift))    mods += "LeftShift+";
        if (IsDown(VirtualKey.RightShift))   mods += "RightShift+";
        if (IsDown(VirtualKey.LeftMenu))     mods += "LeftAlt+";
        if (IsDown(VirtualKey.RightMenu))    mods += "RightAlt+";
        if (IsDown(VirtualKey.LeftWindows))  mods += "LeftWin+";
        if (IsDown(VirtualKey.RightWindows)) mods += "RightWin+";
        return mods;
    }

    private static string? KeyToName(VirtualKey k) => k switch
    {
        VirtualKey.Space  => "Space",
        VirtualKey.Tab    => "Tab",
        VirtualKey.Enter  => "Enter",
        >= VirtualKey.A and <= VirtualKey.Z => k.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)k - (int)VirtualKey.Number0).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => $"F{(int)k - (int)VirtualKey.F1 + 1}",
        _ => null,
    };
}
#endif
