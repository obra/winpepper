#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

public sealed class WasapiRecorder : IAudioRecorder
{
    private const int SampleRate16k = 16000;

    public AudioFormat Format => WinpepperAudioFormat.Mono16k;
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private List<float> _buffer = new();
    private BufferedWaveProvider? _resamplerInput;
    private MediaFoundationResampler? _resampler;
    private bool _disposed;

    public WasapiRecorder(string? deviceId = null, ILogger? log = null)
    {
        _deviceId = deviceId;
        _log = log;
    }

    public void Start()
    {
        WasapiCapture? capture = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(_deviceId);

            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
            capture.DataAvailable += OnData;
            _buffer = new List<float>(SampleRate16k * 30);
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            // Dispose the partially-constructed COM object before rethrowing.
            _log?.LogWarning(ex, "WasapiRecorder failed to start for device {DeviceId}", _deviceId ?? "(default)");
            if (capture is not null)
            {
                capture.DataAvailable -= OnData;
                try { capture.Dispose(); } catch (Exception dex) { _log?.LogDebug(dex, "dispose of partial capture failed"); }
            }
            throw;
        }
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
                    _log?.LogWarning("Dropping meter frame: unsupported format {Encoding} {Bits}-bit",
                        fmt.Encoding, fmt.BitsPerSample);
                    return;
                }

                var frame = fmt.SampleRate == SampleRate16k ? mono : Resample(mono, fmt.SampleRate);
                _buffer.AddRange(frame);
                FramesAvailable?.Invoke(frame);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "WasapiRecorder frame processing failed");
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
        // Bug 1 (duplicate): build the resampler ONCE, not per callback.
        if (_resampler is null || _resamplerInput is null ||
            _resamplerInput.WaveFormat.SampleRate != sourceSampleRate)
        {
            DisposeResampler();
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, 1);
            _resamplerInput = new BufferedWaveProvider(sourceFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
                // Bug 1 (critical): must be false. When true (the NAudio default),
                // Read() zero-pads to the requested count and never returns 0, so
                // MediaFoundationResampler always has input and the drain loop below
                // (`while (_resampler.Read(...) > 0)`) never terminates -> infinite
                // loop + unbounded allocation for any capture rate != 16 kHz (the
                // normal case for real WASAPI mics at 44.1/48 kHz). Setting false
                // makes Read() return only buffered data (down to 0), so the resampler
                // drains and the loop exits, matching the old finite-source behavior.
                ReadFully = false,
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

    public float[] Stop()
    {
        WasapiCapture? capture;
        float[] result;
        lock (_lock)
        {
            capture = _capture;
            _capture = null;
            if (capture is not null) capture.DataAvailable -= OnData;
            result = _buffer.ToArray();
        }
        // Bug 8 (deadlock): run teardown OUTSIDE _lock. capture.Dispose() joins
        // NAudio's capture thread, which may be parked at OnData's `lock (_lock)`;
        // joining while holding _lock would deadlock. Nulling _capture + unhooking
        // above means any later OnData returns before touching the resampler.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
        return result;
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
            // Bug 8: unhook DataAvailable inside the lock so no new callback runs.
            if (capture is not null) capture.DataAvailable -= OnData;
        }
        // Bug 8 (deadlock): StopRecording()/Dispose() run OUTSIDE _lock. Dispose()
        // joins NAudio's capture thread, which may be parked at OnData's
        // `lock (_lock)`; joining while holding _lock would deadlock. _disposed +
        // nulled _capture guarantee any later OnData returns before the resampler.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
    }
}
#endif
