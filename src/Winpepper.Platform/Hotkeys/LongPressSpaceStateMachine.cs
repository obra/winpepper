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
/// Replay-free dual-role Space policy. The original physical down/up always
/// pass through. Reaching the threshold or receiving the first typematic repeat
/// emits HoldDown; repeat downs are then suppressed until the passed-through
/// physical release emits HoldUp.
/// </summary>
public sealed class LongPressSpaceStateMachine : IDisposable
{
    private enum State { Idle, Pending, PassingUntilRelease, Holding }

    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly ILongPressTimerScheduler _timerScheduler;
    private readonly Action<HotkeyEventKind> _emit;
    private readonly Func<bool> _isSpacePhysicallyDown;
    private readonly Func<bool> _canStartHold;
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
        TimeSpan? threshold = null,
        Func<bool>? isSpacePhysicallyDown = null,
        Func<bool>? canStartHold = null)
    {
        _timerScheduler = timerScheduler ?? throw new ArgumentNullException(nameof(timerScheduler));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _isSpacePhysicallyDown = isSpacePhysicallyDown ?? (() => true);
        _canStartHold = canStartHold ?? (() => true);
        _threshold = threshold ?? DefaultThreshold;
        if (_threshold <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(threshold));
    }

    /// <returns>True only for a typematic down that must be suppressed.</returns>
    public bool Process(bool down)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (down)
            {
                if (_state == State.Idle)
                {
                    if (!_canStartHold()) return false;
                    _state = State.Pending;
                    _thresholdTimer = _timerScheduler.Schedule(_threshold, OnThresholdElapsed);
                    return false;
                }

                if (_state == State.Pending)
                {
                    CancelTimerLocked();
                    if (!_canStartHold())
                    {
                        _state = State.PassingUntilRelease;
                        return false;
                    }
                    _state = State.Holding;
                    _emit(HotkeyEventKind.HoldDown);
                    return true;
                }

                return _state == State.Holding;
            }

            if (_state == State.Pending) CancelTimerLocked();
            if (_state == State.Holding) _emit(HotkeyEventKind.HoldUp);
            _state = State.Idle;
            return false;
        }
    }

    /// <summary>
    /// Stops a pending bare-Space observation when a modifier changes the
    /// gesture, while keeping the rest of that already-visible press native.
    /// </summary>
    public bool CancelPendingForModifier()
    {
        lock (_gate)
        {
            if (_disposed || _state != State.Pending) return false;
            CancelTimerLocked();
            _state = _isSpacePhysicallyDown()
                ? State.PassingUntilRelease
                : State.Idle;
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_state == State.Pending) CancelTimerLocked();
            if (_state == State.Holding) _emit(HotkeyEventKind.HoldUp);
            if (_state != State.Idle)
            {
                _state = _isSpacePhysicallyDown()
                    ? State.PassingUntilRelease
                    : State.Idle;
            }
        }
    }

    /// <summary>
    /// Recovers when Windows never delivered the physical Space-up. Call before
    /// processing another transition; the low-level hook's pre-event key state
    /// then distinguishes a repeat from a genuinely new press.
    /// </summary>
    public void RecoverIfReleased()
    {
        lock (_gate)
        {
            if (_disposed || _state == State.Idle || _isSpacePhysicallyDown()) return;
            if (_state == State.Pending) CancelTimerLocked();
            if (_state == State.Holding) _emit(HotkeyEventKind.HoldUp);
            _state = State.Idle;
        }
    }

    private void OnThresholdElapsed()
    {
        lock (_gate)
        {
            _thresholdTimer = null;
            if (_disposed || _state != State.Pending) return;
            if (!_isSpacePhysicallyDown())
            {
                _state = State.Idle;
                return;
            }
            if (!_canStartHold())
            {
                _state = State.PassingUntilRelease;
                return;
            }

            _state = State.Holding;
            // Publish while serialized with Process(key-up). Otherwise key-up
            // can publish HoldUp before this callback publishes HoldDown.
            _emit(HotkeyEventKind.HoldDown);
        }
    }

    private void CancelTimerLocked()
    {
        _thresholdTimer?.Dispose();
        _thresholdTimer = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelTimerLocked();
            if (_state == State.Holding) _emit(HotkeyEventKind.HoldUp);
            _state = State.Idle;
        }
    }
}
