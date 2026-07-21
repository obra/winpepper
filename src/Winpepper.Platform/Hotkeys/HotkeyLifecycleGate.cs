namespace Winpepper.Platform.Hotkeys;

/// <summary>Serializes hotkey startup/capture operations against disposal.</summary>
public sealed class HotkeyLifecycleGate
{
    private readonly object _gate = new();
    private readonly string _ownerName;
    private bool _disposed;

    public HotkeyLifecycleGate(string ownerName)
    {
        if (string.IsNullOrWhiteSpace(ownerName))
            throw new ArgumentException("An owner name is required.", nameof(ownerName));
        _ownerName = ownerName;
    }

    public T Run<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(_ownerName);
            return operation();
        }
    }

    public void Dispose(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cleanup();
        }
    }
}
