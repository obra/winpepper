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

/// <summary>1a: owns the window-context prefetch lifecycle (per-dictation CTS,
/// cancellation ruling). Hwnd captured at listen-START and content too, as of
/// tbc0 (kata tbc0): the launch itself is now driven at listen-start via
/// <see cref="WindowContextListenStartSequencer"/> — the start arm calls
/// <see cref="OnRecordingStart"/> then the sequencer calls <see cref="Start"/>
/// with the start-captured hwnd — while the lifecycle (cancel-prior at the next
/// recording start, cancel-on-drop, teardown) stays here. Per-dictation
/// CancellationTokenSource, cancelled on silence-drop and teardown
/// (<see cref="CancelAndClear"/>) and — per the approved plan's RULING — by the
/// NEXT dictation's recording start (<see cref="OnRecordingStart"/>): live
/// speech wins over a stale context fetch; the prior dictation takes the
/// no-context path and stamps ctx_src=none, an accepted, counted loss. Single
/// caller (the serialized hotkey loop) by contract — no locking. The CTSes
/// carry no timers, so not disposing them is benign.</summary>
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

    /// <summary>Call at recording START, after OnRecordingStart, against the
    /// start-captured hwnd — 1a(b): never re-read focus at stop, or a mid-recording
    /// focus change feeds the WRONG window's content to the cleanup model. The
    /// returned handle is consumed at stop — normally via
    /// <see cref="WindowContextListenStartSequencer"/>, which books the start-launched
    /// handle and hands it back at the stop arm.</summary>
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
