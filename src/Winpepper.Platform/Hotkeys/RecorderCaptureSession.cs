namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Control-neutral owner for one recorder's exclusive raw-capture lease.
/// A failed acquisition never disturbs another recorder's active lease.
/// </summary>
public sealed class RecorderCaptureSession : IDisposable
{
    private readonly Func<Action<RawKeyTransition>, IDisposable> _acquire;
    private IDisposable? _lease;

    public RecorderCaptureSession(Func<Action<RawKeyTransition>, IDisposable> acquire)
        => _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));

    public bool IsActive => _lease is not null;

    public bool TryBegin(Action<RawKeyTransition> sink, out string? error)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (_lease is not null)
        {
            error = null;
            return true;
        }

        try
        {
            _lease = _acquire(sink);
            error = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public void End()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    public void Dispose() => End();
}
