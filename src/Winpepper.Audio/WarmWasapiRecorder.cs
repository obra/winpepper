#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single WasapiCapture runs
/// for the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll). On a device change or
/// capture fault the stream is disposed and lazily recreated on next use.
///
/// This file is #if WINDOWS and cannot be built or run on Linux; the sample
/// bookkeeping it delegates to (WarmCaptureBuffer) is unit-tested, and this thin
/// wiring is verified in the Windows smoke checklist.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    // Ring holds ~1 s so a 500 ms pre-roll always has enough history.
    private const int RingCapacitySamples = SampleRate16k;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly object _captureLock = new();
    private WasapiCapture? _capture;
    private string? _activeDeviceId; // endpoint the live _capture was built on (Bug-2 default-change recheck)

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        if (_prewarm) TryStartCapture();
    }

    public void StartSession(int includePrerollMs)
    {
        // Bug-2 default-device change: a persistent warm WasapiCapture does NOT
        // auto-follow a change of the Windows default input device -- WASAPI does
        // not signal a running capture, so without this check the pill would keep
        // recording the OLD mic. That is a regression vs the previous per-press
        // cold-start, which re-resolved the default endpoint on every press. When
        // we are following the default (no explicit _deviceId), re-resolve it here
        // and rebuild the stream if it drifted. (A fuller solution is an
        // IMMNotificationClient via RegisterEndpointNotificationCallback reacting to
        // OnDefaultDeviceChanged; this per-session recheck is the minimal fix that
        // restores parity and covers the "change default, then dictate" case.)
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode (or a previously faulted warm stream): (re)start capture now.
        if (_capture is null) TryStartCapture();
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    private void RebuildIfDefaultChanged()
    {
        lock (_captureLock)
        {
            if (_capture is null) return; // nothing running; TryStartCapture will pick the current default
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                if (current.ID != _activeDeviceId)
                {
                    // Default moved. _captureLock is a reentrant Monitor, so calling
                    // StopCapture()/TryStartCapture() (which re-take it) is safe here.
                    StopCapture();     // drop the old-device stream
                    TryStartCapture(); // rebuild on the new default
                }
            }
            catch
            {
                // Enumeration failed (e.g. no capture device). Keep the current
                // stream; the fault path / next StartSession retries.
            }
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) StopCapture(); // cold mode tears the stream down between sessions
        return samples;
    }

    private void TryStartCapture()
    {
        lock (_captureLock)
        {
            if (_capture is not null) return;
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = string.IsNullOrEmpty(_deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                    : enumerator.GetDevice(_deviceId);

                var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                _capture = capture;
                _activeDeviceId = device.ID; // remember the endpoint for the default-change recheck
            }
            catch
            {
                // Device unavailable (e.g. unplugged). Leave _capture null; the
                // next StartSession retries. Warm mode simply yields no pre-roll
                // until the device returns.
                _capture = null;
            }
        }
    }

    private void StopCapture()
    {
        lock (_captureLock)
        {
            if (_capture is null) return;
            try { _capture.StopRecording(); } catch { }
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { }
            _capture = null;
            _activeDeviceId = null;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Fault (device change/removal): drop the stream so TryStartCapture
        // rebuilds it on next use.
        if (e.Exception is not null) StopCapture();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null) return;
        var fmt = capture.WaveFormat;

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
            return;
        }

        float[] mono;
        if (fmt.Channels > 1)
        {
            mono = new float[sampleCount / fmt.Channels];
            for (var i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
                mono[i] = sum / fmt.Channels;
            }
        }
        else
        {
            mono = samples;
        }

        if (fmt.SampleRate != SampleRate16k)
        {
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, 1);
            var bytes = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
            var sourceProvider = new RawSourceWaveStream(new MemoryStream(bytes), sourceFormat);
            var resampler = new MediaFoundationResampler(sourceProvider,
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate16k, 1)) { ResamplerQuality = 60 };
            var resampled = new List<float>();
            var byteBuf = new byte[8192];
            int read;
            while ((read = resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
            {
                var floats = new float[read / 4];
                Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
                resampled.AddRange(floats);
            }
            mono = resampled.ToArray();
        }

        _buffer.Ingest(mono);
        if (_buffer.IsSessionActive)
            FramesAvailable?.Invoke(mono);
    }

    public void Dispose() => StopCapture();
}
#endif
