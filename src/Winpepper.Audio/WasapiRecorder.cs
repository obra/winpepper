#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

public sealed class WasapiRecorder : IAudioRecorder
{
    public AudioFormat Format => WinpepperAudioFormat.Mono16k;
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly string? _deviceId;
    private WasapiCapture? _capture;
    private List<float> _buffer = new();

    public WasapiRecorder(string? deviceId = null) { _deviceId = deviceId; }

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(_deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(_deviceId);

        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        _capture.DataAvailable += OnData;
        _buffer = new List<float>(16000 * 30);
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture is null) return;
        var fmt = _capture.WaveFormat;

        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                samples[i] = s / 32768f;
            }
        }
        else
        {
            return;
        }

        // Downmix to mono if needed.
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

        // Resample to 16 kHz if needed.
        if (fmt.SampleRate != 16000)
        {
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, 1);
            var sourceProvider = new RawSourceWaveStream(MemoryStreamFromFloats(mono), sourceFormat);
            var resampler = new MediaFoundationResampler(sourceProvider, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)) { ResamplerQuality = 60 };
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

        lock (_buffer)
        {
            _buffer.AddRange(mono);
        }
        FramesAvailable?.Invoke(mono);
    }

    private static Stream MemoryStreamFromFloats(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }

    public float[] Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        lock (_buffer)
        {
            return _buffer.ToArray();
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}
#endif
