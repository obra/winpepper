namespace Winpepper.Platform.WindowContext;

/// <summary>One dictation's window-context prefetch: the task plus its
/// per-dictation cancellation. Created only by
/// <see cref="WindowContextPrefetchCoordinator.Start"/>.</summary>
public sealed class WindowContextPrefetchHandle
{
    private readonly CancellationTokenSource _cts;

    internal WindowContextPrefetchHandle(Task<WindowContextResult> task, CancellationTokenSource cts)
    {
        Task = task;
        _cts = cts;
        Token = cts.Token;
    }

    public Task<WindowContextResult> Task { get; }

    /// <summary>This dictation's own token — 1a(a)/(d): every dictation gets a
    /// DISTINCT CancellationTokenSource.</summary>
    public CancellationToken Token { get; }

    public bool CancellationRequested => Token.IsCancellationRequested;

    internal void Cancel()
    {
        if (!Task.IsCompleted) _cts.Cancel();
    }
}

/// <summary>1a: owns the window-context prefetch lifecycle after the move to
/// recording-stop. Per-dictation CancellationTokenSource, cancelled on
/// silence-drop and teardown (<see cref="CancelAndClear"/>) and — per the
/// approved plan's RULING — by the NEXT dictation's recording start
/// (<see cref="OnRecordingStart"/>): live speech wins over a stale context
/// fetch; the prior dictation takes the no-context path and stamps
/// ctx_src=none, an accepted, counted loss. Single caller (the serialized
/// hotkey loop) by contract — no locking. The CTSes carry no timers, so not
/// disposing them is benign.</summary>
public sealed class WindowContextPrefetchCoordinator
{
    private readonly Func<IntPtr, CancellationToken, Task<WindowContextResult>> _start;
    private WindowContextPrefetchHandle? _current;

    public WindowContextPrefetchCoordinator(
        Func<IntPtr, CancellationToken, Task<WindowContextResult>> start)
    {
        _start = start;
    }

    /// <summary>The latest launched prefetch, if any (null after
    /// <see cref="CancelAndClear"/> / <see cref="OnRecordingStart"/>).</summary>
    public WindowContextPrefetchHandle? Current => _current;

    /// <summary>Call at every recording START. Cancels a prior dictation's
    /// still-running prefetch (the 1a ruling); a completed one is left alone
    /// and merely cleared.</summary>
    public void OnRecordingStart()
    {
        var prior = _current;
        _current = null;
        prior?.Cancel();
    }

    /// <summary>Call at recording STOP: launch the prefetch against the
    /// window captured at recording start — 1a(b): never re-read focus at
    /// stop, or a mid-recording focus change feeds the WRONG window's content
    /// to the cleanup model.</summary>
    public WindowContextPrefetchHandle Start(IntPtr hwndAtStart)
    {
        var cts = new CancellationTokenSource();
        var handle = new WindowContextPrefetchHandle(_start(hwndAtStart, cts.Token), cts);
        _current = handle;
        return handle;
    }

    /// <summary>Call on silence-drop, session cancel, and teardown — 1a(a):
    /// without this, every silence-dropped dictation would leave a full OCR
    /// burst running.</summary>
    public void CancelAndClear()
    {
        var prior = _current;
        _current = null;
        prior?.Cancel();
    }
}
