namespace Winpepper.Audio;

/// <summary>
/// Test seam over a live audio capture (Bug 4/5). The pure-managed
/// <see cref="WarmCaptureCoordinator"/> drives lifecycle through this interface
/// so the epoch/lock discipline and fault recovery can be unit-tested on Linux
/// with a fake, while the real NAudio implementation
/// (<c>WasapiCaptureSource</c>) stays thin and Windows-only.
///
/// Implementations must already deliver mono 16 kHz float frames — all
/// decode/downmix/resample happens inside the implementation, never here.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Endpoint id the live source was built on (for default-device drift checks).</summary>
    string DeviceId { get; }

    /// <summary>Raised on the capture thread with mono 16 kHz frames.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when capture stops. A non-null argument signals a fault.</summary>
    event Action<Exception?>? Stopped;

    /// <summary>Begin capturing. May throw if the device is unavailable.</summary>
    void Start();
}
