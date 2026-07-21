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
    private enum State { Idle, Pending, Holding }

    public static readonly TimeSpan DefaultThreshold = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly ILongPressTimerScheduler _timerScheduler;
    private readonly Action<HotkeyEventKind> _emit;
    private readonly Action _replaySpace;
    private readonly TimeSpan _threshold;
    private State _state;
    private IDisposable? _thresholdTimer;
    private bool _disposed;

    public LongPressSpaceStateMachine(
        ILongPressTimerScheduler timerScheduler,
        Action<HotkeyEventKind> emit,
        Action replaySpace,
        TimeSpan? threshold = null)
    {
        _timerScheduler = timerScheduler ?? throw new ArgumentNullException(nameof(timerScheduler));
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        _replaySpace = replaySpace ?? throw new ArgumentNullException(nameof(replaySpace));
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
            else
            {
                return false;
            }
        }

        afterLock();
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
                _state = State.Idle;
                if (replayPending) afterLock = _replaySpace;
            }
            else if (_state == State.Holding)
            {
                _state = State.Idle;
                afterLock = () => _emit(HotkeyEventKind.HoldUp);
            }
        }
        afterLock?.Invoke();
    }

    private void OnThresholdElapsed()
    {
        var emit = false;
        lock (_gate)
        {
            _thresholdTimer = null;
            if (_disposed || _state != State.Pending) return;
            _state = State.Holding;
            emit = true;
        }
        if (emit) _emit(HotkeyEventKind.HoldDown);
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
        }
        Cancel(replayPending: false);
    }
}
