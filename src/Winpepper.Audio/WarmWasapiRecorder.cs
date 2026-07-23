#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single capture runs for
/// the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll).
///
/// All lifecycle/concurrency/ring/fault logic lives in the pure-managed,
/// Linux-tested <see cref="WarmCaptureCoordinator"/> behind the
/// <see cref="ICaptureSource"/> seam. This class is the thin Windows shell that
/// supplies the NAudio <see cref="WasapiCaptureSource"/> factory and re-resolves
/// the default input device on session start (WASAPI does not signal a running
/// capture when the default endpoint changes).
///
/// TODO(consolidation): a full IMMNotificationClient via
/// MMDeviceEnumerator.RegisterEndpointNotificationCallback would let us react to
/// OnDefaultDeviceChanged mid-session instead of only at session start. The
/// coordinator already rebuilds on fault with backoff and clears the ring on
/// rebuild, which covers the removal/hiccup cases; the per-session recheck below
/// covers "change default, then dictate". Full notification-client integration
/// is deferred as a clean follow-up because it is Windows-only and cannot be
/// unit-tested on this harness.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    private const int RingCapacitySamples = SampleRate16k; // ~1 s of history

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly WarmCaptureCoordinator _coordinator;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception>? CaptureFaulted;

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null, ILogger? log = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        _log = log;
        _coordinator = new WarmCaptureCoordinator(
            _buffer,
            sourceFactory: () => new WasapiCaptureSource(_deviceId, _log));
        _coordinator.FramesAvailable += f => FramesAvailable?.Invoke(f);
        _coordinator.CaptureFaulted += ex => CaptureFaulted?.Invoke(ex);
        if (_prewarm) _coordinator.EnsureStarted();
    }

    public void StartSession(int includePrerollMs)
    {
        // Follow the default input device: if it drifted since the warm stream
        // was built, rebuild on the new endpoint (clears the ring too).
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode, or a previously faulted warm stream: (re)start now. force
        // bypasses the fault backoff because the user explicitly asked to record.
        _coordinator.EnsureStarted(force: true);
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    private void RebuildIfDefaultChanged()
    {
        if (!_coordinator.IsRunning) return; // nothing live; EnsureStarted picks the current default
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            if (current.ID != _coordinator.ActiveDeviceId)
                _coordinator.Rebuild();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "default-device recheck failed; keeping current warm stream");
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) _coordinator.StopCapture(); // cold mode tears down between sessions
        return samples;
    }

    public void Dispose() => _coordinator.Dispose();
}
#endif
