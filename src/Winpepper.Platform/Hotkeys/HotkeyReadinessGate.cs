namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Shares ASR readiness with the hook boundary while the event loop stays alive
/// for recorder capture. The timestamp also prevents an event queued just before
/// enablement from becoming live afterward.
/// </summary>
public sealed class HotkeyReadinessGate
{
    private readonly object _gate = new();
    private int _enabled;
    private DateTimeOffset _enabledAt;

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void Enable(DateTimeOffset enabledAt)
    {
        lock (_gate)
        {
            _enabledAt = enabledAt;
            Volatile.Write(ref _enabled, 1);
        }
    }

    public void Disable()
    {
        Volatile.Write(ref _enabled, 0);
    }

    public bool ShouldHandle(HotkeyEvent evt)
    {
        lock (_gate) return IsEnabled && evt.Timestamp >= _enabledAt;
    }
}
