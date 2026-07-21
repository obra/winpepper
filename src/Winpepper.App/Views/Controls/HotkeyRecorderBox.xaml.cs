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
    public Func<Action<RawKeyTransition>, IDisposable>? CaptureRequested { get; set; }

    private readonly ChordRecorder _recorder = new();
    private string _chordBeforeRecording = "";
    private ILogger? _logCache;
    private ILogger? Log => _logCache ??= App.Shell?.LogFactory.CreateLogger("HotkeyRecorderBox");

    private RecorderCaptureSession? _captureSession;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata("Hotkey",
            (d, e) => ((HotkeyRecorderBox)d).LabelBlock.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty HintProperty =
        DependencyProperty.Register(nameof(Hint), typeof(string), typeof(HotkeyRecorderBox),
            new PropertyMetadata("", (d, e) =>
            {
                var box = (HotkeyRecorderBox)d;
                box.HintBlock.Text = (string)e.NewValue;
                box.HintBlock.Visibility = string.IsNullOrWhiteSpace((string)e.NewValue)
                    ? Visibility.Collapsed : Visibility.Visible;
            }));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Unloaded += OnUnloaded;
        IsTabStop = true;
    }

    // Torn down (page navigated away, window closed) - release this control's
    // capture lease and stop any in-flight recording.
    private void OnUnloaded(object sender, RoutedEventArgs e)
        => CancelCapture("control unloaded");

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
        if (CaptureRequested is not null)
        {
            _captureSession = new RecorderCaptureSession(CaptureRequested);
            if (!_captureSession.TryBegin(OnRawKeyTransition, out var error))
            {
                _captureSession.Dispose();
                _captureSession = null;
                _recorder.Cancel();
                SetChord(_chordBeforeRecording,
                    string.IsNullOrWhiteSpace(error)
                        ? "Another hotkey recorder is already active."
                        : error);
                ResetButtons();
                Log?.LogWarning("Hotkey recording could not start ({Label}): {Error}", Label, error);
                return;
            }
        }
        ChordText.Text = "(press a chord - Esc cancels)";
        RecordButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        Log?.LogInformation("Hotkey recording started ({Label})", Label);
        Focus(FocusState.Programmatic);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRecording("cancel button");

    public void CancelCapture(string reason = "cancelled") => CancelRecording(reason);

    private void CancelRecording(string reason)
    {
        var cancelled = _recorder.Cancel();
        EndCapture();
        if (!cancelled) return;
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
        // Production capture comes from the low-level hook and does not depend
        // on this control retaining focus. Keep routed keys as a fallback for
        // isolated control hosts that do not wire a capture provider.
        if (!_recorder.IsRecording || CaptureRequested is not null) return;

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
        if (!_recorder.IsRecording || CaptureRequested is not null || !IsModifierKey(e.Key)) return;

        // Modifier-only chords are complete when the user begins releasing
        // them. The recorder retained the largest modifier set observed on
        // key-down, before InputKeyboardSource dropped the released key.
        HandleRecorderResult(_recorder.OnModifierKeyUp(), e);
    }

    private void OnRawKeyTransition(RawKeyTransition transition)
    {
        // The hook callback runs on its dedicated native thread. WinUI controls
        // may only be touched from the page DispatcherQueue.
        DispatcherQueue.TryEnqueue(() => HandleRecorderResult(_recorder.OnRawKey(transition)));
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
                EndCapture();
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

    private void HandleRecorderResult(ChordKeyResult result)
    {
        switch (result)
        {
            case ChordKeyResult.Cancelled:
                EndCapture();
                ChordText.Text = _chordBeforeRecording;
                ResetButtons();
                Log?.LogInformation("Hotkey recording cancelled ({Label}): Esc", Label);
                break;
            case ChordKeyResult.Committed:
                CommitRecordedChord();
                break;
            case ChordKeyResult.Invalid:
                SetChord("(invalid)", "Could not parse that combination.");
                break;
        }
    }

    private void CommitRecordedChord()
    {
        var chord = _recorder.CommittedChord!;
        SetChord(chord, null);
        ResetButtons();
        ChordRecorded?.Invoke(chord);
        EndCapture();
        Log?.LogInformation("Hotkey recording committed ({Label}): {Chord}", Label, chord);
    }

    private void EndCapture()
    {
        _captureSession?.Dispose();
        _captureSession = null;
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

    private static string? KeyToName(VirtualKey key)
        => VirtualKeyCatalog.TryGetRecordableKeyName((int)key, out var name) ? name : null;
}
#endif
