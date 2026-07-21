namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Lets the hook/event loop stay alive for recorder capture while discarding
/// normal triggers until ASR is ready. The readiness timestamp prevents an
/// event queued before enablement from becoming live afterward.
/// </summary>
public sealed class HotkeyReadinessGate
{
    private readonly object _gate = new();
    private bool _enabled;
    private DateTimeOffset _enabledAt;

    public void Enable(DateTimeOffset enabledAt)
    {
        lock (_gate)
        {
            _enabledAt = enabledAt;
            _enabled = true;
        }
    }

    public void Disable()
    {
        lock (_gate) _enabled = false;
    }

    public bool ShouldHandle(HotkeyEvent evt)
    {
        lock (_gate) return _enabled && evt.Timestamp >= _enabledAt;
    }
}
