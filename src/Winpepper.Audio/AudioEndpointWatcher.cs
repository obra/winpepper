#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Winpepper.Audio;

/// <summary>
/// Thin Windows-only shell over WASAPI endpoint notifications
/// (MMDeviceEnumerator.RegisterEndpointNotificationCallback). It exists for one
/// reason: after a sleep/resume there is a window where no default capture
/// endpoint exists, the warm stream's immediate rebuild fails
/// (0x80070490 "Element not found"), and nothing retries until the user presses
/// a hotkey - which, if the keyboard hook also died, never happens.
///
/// CONTRACT: IMMNotificationClient callbacks arrive on COM/MTA threads and
/// must never block. Rebuilding capture takes a lock and can dispose a source
/// (which joins a capture thread), so the handler is ALWAYS marshalled onto the
/// thread pool and the callback thread returns immediately. This includes
/// resolving an endpoint's DataFlow (IMMDeviceEnumerator::GetDevice +
/// IMMEndpoint::GetDataFlow are blocking COM round-trips, and the field
/// enumerator's RCW has UI-thread affinity) - NO COM call is made on the
/// callback thread, and no MMDevice is disposed inside one. NOTE: the hand-off
/// DE-serializes the callbacks - several handlers can run concurrently, which
/// is why every recovery decision lives behind CaptureRecoveryPolicy's lock.
/// No decision logic lives here - see <see cref="CaptureRecoveryPolicy"/>.
///
/// LOGGING: every endpoint notification is logged so a Windows smoke run (and
/// the next field incident) yields EVIDENCE, not just pass/fail. Endpoint IDs
/// are opaque GUID strings - never user content - so this respects the
/// content-free logging constraint.
/// </summary>
public sealed class AudioEndpointWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action _onCaptureEndpointChanged;
    private readonly ILogger? _log;
    private int _disposed;

    public AudioEndpointWatcher(Action onCaptureEndpointChanged, ILogger? log = null)
    {
        _onCaptureEndpointChanged = onCaptureEndpointChanged
            ?? throw new ArgumentNullException(nameof(onCaptureEndpointChanged));
        _log = log;
        // NAudio's wrapper passes the COM HRESULT through instead of throwing;
        // ignoring it would log "Subscribed..." for a registration that never
        // fires a single callback.
        var hr = _enumerator.RegisterEndpointNotificationCallback(this);
        if (hr != 0)
            _log?.LogWarning("RegisterEndpointNotificationCallback failed: 0x{Hr:X}", hr);
        else
            _log?.LogInformation("Subscribed to audio endpoint notifications");
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // A NULL default-device id is the diagnostic signature of the
        // mid-resume "no default capture endpoint yet" window (the incident).
        // The thread id shows callbacks arrive off the UI thread.
        _log?.LogInformation(
            "Default audio device changed: flow={Flow} role={Role} device={DeviceId} thread={ThreadId}",
            flow, role, defaultDeviceId ?? "<none>", Environment.CurrentManagedThreadId);
        if (flow == DataFlow.Capture) Signal();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        _log?.LogInformation(
            "Audio device state changed: device={DeviceId} state={State} thread={ThreadId}",
            deviceId, newState, Environment.CurrentManagedThreadId);
        if (newState != DeviceState.Active) return;
        // This callback carries NO DataFlow, so it fires for RENDER endpoints
        // too (Bluetooth headphones, monitor speakers going Active). Signaling
        // on those would drive a spurious rebuild attempt on every unrelated
        // audio device connect/disconnect - so the flow must be resolved.
        //
        // BUT NOT HERE. Resolving it means IMMDeviceEnumerator::GetDevice +
        // IMMEndpoint::GetDataFlow, i.e. blocking COM round-trips, and
        // MS's IMMNotificationClient guidance is explicit that the client must
        // not block in a callback and must not release the last reference to
        // an MMDevice API object inside one. Worse, `_enumerator` is created
        // in the WarmWasapiRecorder constructor, which PipelineHost.TryStartCore
        // (PipelineHost.cs:262-267) runs on the app's STA/UI thread: calling it
        // from this MTA callback thread marshals back through the UI thread's
        // pump, blocking the endpoint notification thread on the very UI thread
        // the Task 9 smoke claims we are immune to. Hand off FIRST; resolve on
        // the thread pool with a thread-local enumerator.
        SignalIfCapture(deviceId);
    }

    public void OnDeviceAdded(string pwstrDeviceId)
        => _log?.LogDebug("Audio device added: device={DeviceId}", pwstrDeviceId);

    public void OnDeviceRemoved(string deviceId)
        => _log?.LogDebug("Audio device removed: device={DeviceId}", deviceId);

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _onCaptureEndpointChanged(); }
            catch (Exception ex) { _log?.LogWarning(ex, "audio endpoint change handler failed"); }
        });
    }

    /// <summary>
    /// Hand off FIRST, then resolve the endpoint's data flow on the thread
    /// pool - NEVER on the IMMNotificationClient callback thread. Uses a
    /// SHORT-LIVED, locally-created enumerator rather than the field: the
    /// field's RCW was created on the app's STA/UI thread, so calling it from
    /// here would marshal back through that thread's message pump. A local
    /// enumerator created on this MTA pool thread has no such affinity, and
    /// disposing the MMDevice here is outside any COM callback.
    /// </summary>
    private void SignalIfCapture(string deviceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                if (device.DataFlow != DataFlow.Capture) return;
            }
            catch (Exception ex)
            {
                // The device can vanish again mid-churn; content-free. Bail
                // rather than guess - OnDefaultDeviceChanged still covers the
                // incident's actual signature (a capture default reappearing).
                _log?.LogDebug(ex, "could not resolve data flow for a state-changed device");
                return;
            }
            try { _onCaptureEndpointChanged(); }
            catch (Exception ex) { _log?.LogWarning(ex, "audio endpoint change handler failed"); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (Exception ex) { _log?.LogDebug(ex, "unregister endpoint notification failed"); }
        try { _enumerator.Dispose(); }
        catch (Exception ex) { _log?.LogDebug(ex, "endpoint enumerator dispose failed"); }
    }
}
#endif
