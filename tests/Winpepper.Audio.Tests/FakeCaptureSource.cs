using Winpepper.Audio;

namespace Winpepper.Audio.Tests;

/// <summary>
/// Deterministic in-memory capture seam for coordinator tests. Lets a test fire
/// synthetic frames and faults. <see cref="RaiseFrame"/> deliberately does NOT
/// throw when disposed — it models a *late* capture-thread callback arriving
/// after teardown, exactly the race the coordinator's epoch guard must absorb.
/// The guard is what's under test: if the coordinator ever touched a disposed
/// source's members it would be observable (see the sabotage step in the
/// concurrency hammer), but under the correct guard no frame is ever routed into
/// a disposed instance.
/// </summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    private volatile bool _disposed;

    public FakeCaptureSource(string deviceId = "fake-device") { DeviceId = deviceId; }

    public string DeviceId { get; }
    public bool Disposed => _disposed;
    public bool Started { get; private set; }
    public bool ThrowOnStart { get; set; }

    /// <summary>Simulated capture-callback thread id (set by tests that model self-join scenarios).</summary>
    public int? CallbackThreadId { get; set; }

    /// <summary>Managed thread id observed by <see cref="Dispose"/>.</summary>
    public int? DisposedOnThreadId { get; private set; }

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        if (ThrowOnStart) throw new InvalidOperationException("fake start failure");
        Started = true;
    }

    /// <summary>Simulate a capture-thread frame callback (may arrive after Dispose).</summary>
    public void RaiseFrame(float[] frame) => FramesAvailable?.Invoke(frame);

    /// <summary>Simulate a frame callback as if raised from the capture thread (functionally identical to <see cref="RaiseFrame"/>; named for intent in the test).</summary>
    public void RaiseFrameFromCaptureThread(float[] frame) => FramesAvailable?.Invoke(frame);

    /// <summary>Simulate the source stopping (fault when <paramref name="ex"/> is non-null).</summary>
    public void RaiseStopped(Exception? ex) => Stopped?.Invoke(ex);

    public void Dispose()
    {
        DisposedOnThreadId = Environment.CurrentManagedThreadId;
        _disposed = true;
    }
}
