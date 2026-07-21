#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

/// <summary>
/// Thin NAudio-backed <see cref="ICaptureSource"/> (Bugs 1/3/5). Owns exactly one
/// <see cref="WasapiCapture"/> and — critically — exactly ONE
/// <see cref="MediaFoundationResampler"/> per capture format, fed by a reusable
/// <see cref="BufferedWaveProvider"/>. The old code built a resampler in every
/// ~50 ms callback and never disposed it (~72k leaked COM objects/hour); here it
/// is created once when the format is known and disposed on teardown.
///
/// Decode/downmix/resample all happen here so the coordinator only ever sees
/// mono 16 kHz frames. <see cref="OnData"/> and <see cref="Dispose"/> are made
/// mutually safe with a lock + disposed flag, mirroring the epoch discipline the
/// coordinator unit-tests on Linux. NOTE: <see cref="Dispose"/> performs its
/// bookkeeping (set disposed, unhook, null the capture) under the lock but runs
/// the actual teardown (StopRecording/Dispose, which JOINS NAudio's capture
/// thread) OUTSIDE the lock. OnData holds the same lock, so joining while holding
/// it would deadlock. This teardown-vs-callback mutual exclusion is the one piece
/// of concurrency the Linux hammer (Task 4) does NOT cover — it lives only here
/// and is exercised in the Windows smoke stress loop (S5).
/// </summary>
public sealed class WasapiCaptureSource : ICaptureSource
{
    private const int SampleRate16k = 16000;

    private readonly string? _requestedDeviceId;
    private readonly ILogger? _log;
    private readonly object _lock = new();

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _resamplerInput;
    private MediaFoundationResampler? _resampler;
    private bool _disposed;

    public WasapiCaptureSource(string? deviceId, ILogger? log = null)
    {
        _requestedDeviceId = deviceId;
        _log = log;
    }

    public string DeviceId { get; private set; } = "";

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        WasapiCapture? capture = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(_requestedDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(_requestedDeviceId);

            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _capture = capture;
            DeviceId = device.ID;
        }
        catch (Exception ex)
        {
            // Bug 5: dispose the partially-constructed COM AudioClient before
            // rethrowing so a flaky-hardware retry loop does not leak a live mic.
            _log?.LogWarning(ex, "WASAPI capture failed to start for device {DeviceId}", _requestedDeviceId ?? "(default)");
            if (capture is not null)
            {
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.Dispose(); } catch (Exception dex) { _log?.LogDebug(dex, "dispose of partial WASAPI capture failed"); }
            }
            throw;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _log?.LogWarning(e.Exception, "WASAPI capture stopped with fault");
        Stopped?.Invoke(e.Exception);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var capture = _capture;
            if (capture is null) return;
            var fmt = capture.WaveFormat;

            try
            {
                var mono = DecodeToMono(e, fmt);
                if (mono is null)
                {
                    // Bug 3: an unsupported format used to be dropped silently.
                    _log?.LogWarning("Dropping capture frame: unsupported format {Encoding} {Bits}-bit",
                        fmt.Encoding, fmt.BitsPerSample);
                    return;
                }

                var frame = fmt.SampleRate == SampleRate16k ? mono : Resample(mono, fmt.SampleRate);
                FramesAvailable?.Invoke(frame);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "WASAPI frame processing failed");
            }
        }
    }

    private static float[]? DecodeToMono(WaveInEventArgs e, WaveFormat fmt)
    {
        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return null;
        }

        if (fmt.Channels <= 1) return samples;

        var mono = new float[sampleCount / fmt.Channels];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
            mono[i] = sum / fmt.Channels;
        }
        return mono;
    }

    private float[] Resample(float[] mono, int sourceSampleRate)
    {
        // Bug 1: build the resampler ONCE (per source rate), fed by a reusable
        // BufferedWaveProvider, instead of allocating one per callback.
        if (_resampler is null || _resamplerInput is null ||
            _resamplerInput.WaveFormat.SampleRate != sourceSampleRate)
        {
            DisposeResampler();
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, 1);
            _resamplerInput = new BufferedWaveProvider(sourceFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            _resampler = new MediaFoundationResampler(
                _resamplerInput, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate16k, 1))
            { ResamplerQuality = 60 };
        }

        var inBytes = new byte[mono.Length * 4];
        Buffer.BlockCopy(mono, 0, inBytes, 0, inBytes.Length);
        _resamplerInput.AddSamples(inBytes, 0, inBytes.Length);

        var resampled = new List<float>();
        var byteBuf = new byte[8192];
        int read;
        while ((read = _resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
        {
            var floats = new float[read / 4];
            Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
            resampled.AddRange(floats);
        }
        return resampled.ToArray();
    }

    private void DisposeResampler()
    {
        try { _resampler?.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "resampler dispose failed"); }
        _resampler = null;
        _resamplerInput = null;
    }

    public void Dispose()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            capture = _capture;
            _capture = null;
            if (capture is not null)
            {
                // Unhook INSIDE the lock so no new callback is dispatched. Any
                // in-flight OnData either already holds the lock (we wait for it) or
                // will see _disposed and return at the top.
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnRecordingStopped;
            }
        }
        // Bug 4 (deadlock): tear down OUTSIDE _lock. capture.Dispose() calls
        // WasapiCapture's captureThread.Join(); that capture thread may be parked at
        // OnData's `lock (_lock)`. Joining while holding _lock is a lock->Join vs
        // OnData-waiting-on-lock inversion => intermittent hang on rebuild/teardown.
        // Once _disposed is set and handlers are unhooked above, no OnData touches
        // _capture or the resampler, so this teardown is race-free without the lock.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
    }
}
#endif
