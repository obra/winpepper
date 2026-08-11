namespace Winpepper.Platform.WindowContext;

/// <summary>tbc0: owns the listen-start launch / stop-consume handle book so
/// PipelineHost's two hotkey arms are one-line delegations (unit-tested here on
/// Linux; the arms themselves are #if WINDOWS). Launch happens HERE, at
/// RecordingStarted — never at RecordingStopped. The launch DECISION is evaluated
/// by the caller (Winpepper.Cleanup.WindowContextListenStartPolicy) and arrives as
/// <paramref name="startPrefetch"/>; the coordinator lifecycle (cancel-prior,
/// cancel-on-drop) stays with <see cref="WindowContextPrefetchCoordinator"/>.
/// Single caller (the serialized hotkey loop) by contract — no locking.</summary>
public sealed class WindowContextListenStartSequencer
{
    private readonly WindowContextPrefetchCoordinator _coordinator;
    private WindowContextPrefetchHandle? _launched;

    public WindowContextListenStartSequencer(WindowContextPrefetchCoordinator coordinator)
        => _coordinator = coordinator;

    /// <summary>Call at listen-start, AFTER OnRecordingStart() and the hwnd capture.
    /// Launches (and books) only when <paramref name="startPrefetch"/> is true;
    /// otherwise launches nothing and books null.</summary>
    public WindowContextPrefetchHandle? RecordingStarted(bool startPrefetch, IntPtr hwndAtStart)
    {
        _launched = startPrefetch ? _coordinator.Start(hwndAtStart) : null;
        return _launched;
    }

    /// <summary>Call at stop: hands the listen-start handle to the consume path
    /// exactly once and clears the book. Never launches.</summary>
    public WindowContextPrefetchHandle? RecordingStopped()
    {
        var h = _launched;
        _launched = null;
        return h;
    }

    /// <summary>Clear the book without consuming (cancel / silence-drop / teardown).
    /// The underlying task, if any, is cancelled by the caller's existing
    /// coordinator.CancelAndClear() discipline.</summary>
    public void Clear() => _launched = null;
}
