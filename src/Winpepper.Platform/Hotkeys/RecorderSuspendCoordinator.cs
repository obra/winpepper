namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Guarantees the global hotkey hook is never left suspended when a hotkey
/// recorder control is torn down. The recorder UI (HotkeyRecorderBox) is
/// WinUI-only and hard to test, so the "suspend must be released on teardown"
/// rule lives here as pure managed logic. <see cref="SetRecording"/> forwards
/// only real state transitions to the sink; <see cref="Teardown"/> always
/// drives the sink back to "not suspended" if it is still suspended.
/// </summary>
public sealed class RecorderSuspendCoordinator
{
    private readonly Action<bool> _suspendSink;
    private bool _suspended;

    public RecorderSuspendCoordinator(Action<bool> suspendSink)
        => _suspendSink = suspendSink ?? throw new ArgumentNullException(nameof(suspendSink));

    /// <summary>True while the hook is suspended on this recorder's behalf.</summary>
    public bool IsSuspended => _suspended;

    /// <summary>
    /// The recorder started (true) or stopped (false) capturing. Idempotent:
    /// only a real transition is forwarded to the suspend sink.
    /// </summary>
    public void SetRecording(bool recording)
    {
        if (recording == _suspended) return;
        _suspended = recording;
        _suspendSink(recording);
    }

    /// <summary>
    /// Called on recorder Unloaded / dispose / window close. Releases suspend if
    /// it is still held, so a torn-down recorder can never leave hotkeys dead.
    /// </summary>
    public void Teardown()
    {
        if (!_suspended) return;
        _suspended = false;
        _suspendSink(false);
    }
}
