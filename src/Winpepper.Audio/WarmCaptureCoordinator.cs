using System.Threading;

namespace Winpepper.Audio;

/// <summary>
/// Pure-managed lifecycle owner for warm capture (Bugs 4/5/6/7). Holds the live
/// <see cref="ICaptureSource"/> behind a lock, routes frames into the
/// <see cref="WarmCaptureBuffer"/>, and makes teardown mutually safe with the
/// lock-free capture callback:
///
///  * Frames carry their originating source; <see cref="OnSourceFrame"/> reads
///    the current source once and drops the frame if it no longer matches
///    (epoch guard), so a late callback from a disposed source is ignored.
///  * Teardown swaps the reference to null BEFORE disposing, so the callback
///    can never observe a half-disposed source.
///  * A partially-constructed source is disposed if <see cref="ICaptureSource.Start"/>
///    throws (Bug 5).
///  * The ring is cleared on every rebuild (Bug 6).
///  * A fault triggers a logged rebuild attempt, rate-limited by a backoff so a
///    storming device does not spin (Bug 7).
/// </summary>
public sealed class WarmCaptureCoordinator : IDisposable
{
    private readonly WarmCaptureBuffer _buffer;
    private readonly Func<ICaptureSource> _sourceFactory;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _faultBackoff;
    private readonly object _lock = new();

    private ICaptureSource? _current;   // read lock-free in OnSourceFrame via Volatile
    private string? _activeDeviceId;
    private DateTime? _lastFaultUtc;
    private bool _disposed;

    public WarmCaptureCoordinator(
        WarmCaptureBuffer buffer,
        Func<ICaptureSource> sourceFactory,
        Func<DateTime>? clock = null,
        TimeSpan? faultBackoff = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _clock = clock ?? (() => DateTime.UtcNow);
        _faultBackoff = faultBackoff ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Re-raised (mono 16 kHz) only while a session is active.</summary>
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when capture faults or fails to (re)start (Bug 3).</summary>
    public event Action<Exception>? CaptureFaulted;

    public bool IsRunning => Volatile.Read(ref _current) is not null;
    public string? ActiveDeviceId { get { lock (_lock) return _activeDeviceId; } }

    /// <summary>
    /// Start capture if not already running. <paramref name="force"/> bypasses
    /// the fault backoff — used when the user explicitly starts a session on a
    /// previously faulted stream (Bug 7).
    /// </summary>
    public void EnsureStarted(bool force = false)
    {
        Exception? fault = null;
        lock (_lock)
        {
            if (_disposed || _current is not null) return;
            if (!force && InBackoffLocked()) return;
            fault = StartLocked();
        }
        if (fault is not null) CaptureFaulted?.Invoke(fault);
    }

    /// <summary>Tear down the current source and build a fresh one on the current default (Bug 6/7).</summary>
    public void Rebuild()
    {
        Exception? fault = null;
        lock (_lock)
        {
            if (_disposed) return;
            SwapOutAndDisposeLocked();
            _buffer.Clear();            // Bug 6: no stale-device pre-roll
            fault = StartLocked();
        }
        if (fault is not null) CaptureFaulted?.Invoke(fault);
    }

    /// <summary>Stop and dispose the current source (used by cold-mode teardown).</summary>
    public void StopCapture()
    {
        lock (_lock) SwapOutAndDisposeLocked();
    }

    // --- internals -----------------------------------------------------------

    private bool InBackoffLocked()
        => _lastFaultUtc is { } last && (_clock() - last) <= _faultBackoff;

    /// <summary>Build+subscribe+start under the lock. Returns a fault to raise after unlocking.</summary>
    private Exception? StartLocked()
    {
        ICaptureSource? src = null;
        try
        {
            src = _sourceFactory();
            src.FramesAvailable += f => OnSourceFrame(src, f);
            src.Stopped += ex => OnSourceStopped(src, ex);
            src.Start();
            Volatile.Write(ref _current, src);
            _activeDeviceId = src.DeviceId;
            return null;
        }
        catch (Exception ex)
        {
            try { src?.Dispose(); } catch { /* best-effort teardown of partial source (Bug 5) */ }
            _lastFaultUtc = _clock();
            return ex;
        }
    }

    /// <summary>Swap the reference to null BEFORE disposing so callbacks bail early.</summary>
    private void SwapOutAndDisposeLocked()
    {
        var old = _current;
        Volatile.Write(ref _current, null);
        _activeDeviceId = null;
        if (old is not null) { try { old.Dispose(); } catch { /* best-effort */ } }
    }

    private void OnSourceFrame(ICaptureSource source, ReadOnlyMemory<float> frame)
    {
        // Epoch guard: read the live reference once. If this callback belongs to
        // a source that has since been swapped out, drop it — we never touch the
        // (possibly disposed) source object here, only the frame payload.
        if (!ReferenceEquals(source, Volatile.Read(ref _current))) return;
        _buffer.Ingest(frame.Span);
        if (_buffer.IsSessionActive) FramesAvailable?.Invoke(frame);
    }

    private void OnSourceStopped(ICaptureSource source, Exception? ex)
    {
        if (ex is null) return; // clean stop, nothing to recover
        bool retry;
        Exception? startFault = null;
        lock (_lock)
        {
            if (!ReferenceEquals(source, _current)) return; // already replaced
            SwapOutAndDisposeLocked();
            var now = _clock();
            retry = _lastFaultUtc is not { } last || (now - last) > _faultBackoff;
            _lastFaultUtc = now;
            if (retry && !_disposed) startFault = StartLocked();
        }
        CaptureFaulted?.Invoke(ex);
        if (startFault is not null) CaptureFaulted?.Invoke(startFault);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            SwapOutAndDisposeLocked();
        }
    }
}
