namespace Winpepper.Platform.Hotkeys;

public interface ILongPressTimerScheduler
{
    IDisposable Schedule(TimeSpan dueTime, Action callback);
}

internal sealed class SystemLongPressTimerScheduler : ILongPressTimerScheduler
{
    public IDisposable Schedule(TimeSpan dueTime, Action callback)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            callback();
        }, null, dueTime, Timeout.InfiniteTimeSpan);
        return timer;
    }
}

/// <summary>
/// Deterministic dual-role Space policy: a short physical press is replayed,
/// while reaching the threshold emits HoldDown until physical release.
/// </summary>
public sealed class LongPressSpaceStateMachine : IDisposable
{
    private enum State { Idle, Pending, SuppressingUntilRelease, Holding }

    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly ILongPressTimerScheduler _timerScheduler;
    private readonly Action<HotkeyEventKind> _emit;
    private readonly Action _replaySpace;
    private readonly Func<bool> _isSpacePhysicallyDown;
    private readonly TimeSpan _threshold;
    private State _state;
    private IDisposable? _thresholdTimer;
    private bool _disposed;

    public bool IsActive
    {
        get { lock (_gate) return _state != State.Idle; }
    }

    public LongPressSpaceStateMachine(
        ILongPressTimerScheduler timerScheduler,
        Action<HotkeyEventKind> emit,
        Action replaySpace,
        TimeSpan? threshold = null,
        Func<bool>? isSpacePhysicallyDown = null)
    {
        _timerScheduler = timerScheduler ?? throw new ArgumentNullException(nameof(timerScheduler));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _replaySpace = replaySpace ?? throw new ArgumentNullException(nameof(replaySpace));
        _isSpacePhysicallyDown = isSpacePhysicallyDown ?? (() => true);
        _threshold = threshold ?? DefaultThreshold;
        if (_threshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
    }

    /// <returns>True when the physical transition must be swallowed.</returns>
    public bool Process(bool down, bool isOwnReplay)
    {
        if (isOwnReplay) return false;

        Action? afterLock = null;
        lock (_gate)
        {
            if (_disposed) return false;
            if (down)
            {
                if (_state == State.Idle)
                {
                    _state = State.Pending;
                    _thresholdTimer = _timerScheduler.Schedule(_threshold, OnThresholdElapsed);
                }
                // A second down while pending/holding is typematic repeat.
                return true;
            }

            if (_state == State.Pending)
            {
                CancelTimerLocked();
                _state = State.Idle;
                afterLock = _replaySpace;
            }
            else if (_state == State.Holding)
            {
                _state = State.Idle;
                afterLock = () => _emit(HotkeyEventKind.HoldUp);
            }
            else if (_state == State.SuppressingUntilRelease)
            {
                _state = State.Idle;
            }
            else
            {
                return false;
            }
        }

        afterLock?.Invoke();
        return true;
    }

    /// <summary>
    /// Converts a pending bare tap into a replay while retaining ownership of
    /// the physical Space press until its eventual key-up.
    /// </summary>
    public bool CancelPendingForModifier()
    {
        Action? replay = null;
        lock (_gate)
        {
            if (_disposed || _state != State.Pending) return false;
            CancelTimerLocked();
            _state = _isSpacePhysicallyDown()
                ? State.SuppressingUntilRelease
                : State.Idle;
            replay = _replaySpace;
        }
        replay!();
        return true;
    }

    public void Cancel(bool replayPending)
    {
        Action? afterLock = null;
        lock (_gate)
        {
            if (_state == State.Pending)
            {
                CancelTimerLocked();
                _state = _isSpacePhysicallyDown()
                    ? State.SuppressingUntilRelease
                    : State.Idle;
                if (replayPending) afterLock = _replaySpace;
            }
            else if (_state == State.Holding)
            {
                _state = _isSpacePhysicallyDown()
                    ? State.SuppressingUntilRelease
                    : State.Idle;
                _emit(HotkeyEventKind.HoldUp);
            }
            else if (_state == State.SuppressingUntilRelease)
            {
                if (!_isSpacePhysicallyDown()) _state = State.Idle;
            }
        }
        afterLock?.Invoke();
    }

    /// <summary>
    /// Recovers when Windows never delivered the physical Space-up. Call before
    /// processing another transition; the low-level hook's pre-event key state
    /// then distinguishes a repeat from a genuinely new press.
    /// </summary>
    public void RecoverIfReleased()
    {
        Action? afterLock = null;
        lock (_gate)
        {
            if (_disposed || _state == State.Idle || _isSpacePhysicallyDown()) return;
            if (_state == State.Pending)
            {
                CancelTimerLocked();
                _state = State.Idle;
                afterLock = _replaySpace;
            }
            else if (_state == State.Holding)
            {
                _state = State.Idle;
                _emit(HotkeyEventKind.HoldUp);
            }
            else
            {
                _state = State.Idle;
            }
        }
        afterLock?.Invoke();
    }

    private void OnThresholdElapsed()
    {
        Action? afterLock = null;
        lock (_gate)
        {
            _thresholdTimer = null;
            if (_disposed || _state != State.Pending) return;
            if (!_isSpacePhysicallyDown())
            {
                _state = State.Idle;
                afterLock = _replaySpace;
            }
            else
            {
                _state = State.Holding;
                // Publish while serialized with Process(key-up). Otherwise key-up
                // can observe Holding and publish HoldUp before this callback gets
                // a chance to publish HoldDown.
                _emit(HotkeyEventKind.HoldDown);
            }
        }
        afterLock?.Invoke();
    }

    private void CancelTimerLocked()
    {
        _thresholdTimer?.Dispose();
        _thresholdTimer = null;
    }

    public void Dispose()
    {
        var emitHoldUp = false;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelTimerLocked();
            emitHoldUp = _state == State.Holding;
            _state = State.Idle;
        }
        if (emitHoldUp) _emit(HotkeyEventKind.HoldUp);
    }
}
