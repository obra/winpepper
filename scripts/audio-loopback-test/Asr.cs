// Full audio + ASR loopback: WasapiRecorder -> ParakeetSession.
// Run via `dotnet run -- asr` and inject TTS audio via paplay during the 5s window.
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Winpepper.Asr;

static class Asr
{
    public static async Task Run()
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "winpepper", "models", "parakeet-tdt-0.6b-v3");
        if (!Directory.Exists(modelDir))
        {
            Console.WriteLine($"Model dir missing: {modelDir}");
            return;
        }

        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        Console.WriteLine($"Mic: {device.FriendlyName}");

        var capture = new WasapiCapture(device, true, 50);
        var samples16k = new List<float>();
        var fmt = capture.WaveFormat;
        Console.WriteLine($"Native format: {fmt}");

        capture.DataAvailable += (s, e) =>
        {
            // Decode device bytes to float32 mono samples.
            int sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
            float[] mono;
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
            {
                var stereo = new float[sampleCount];
                Buffer.BlockCopy(e.Buffer, 0, stereo, 0, e.BytesRecorded);
                mono = Downmix(stereo, fmt.Channels);
            }
            else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
            {
                var s16 = new float[sampleCount];
                for (var i = 0; i < sampleCount; i++)
                    s16[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                mono = Downmix(s16, fmt.Channels);
            }
            else return;

            // Resample to 16k via MediaFoundationResampler (one-shot per chunk).
            if (fmt.SampleRate != 16000)
            {
                var src = new WaveFormatRawSource(mono, fmt.SampleRate);
                using var resampler = new MediaFoundationResampler(src, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)) { ResamplerQuality = 60 };
                var byteBuf = new byte[8192];
                int read;
                while ((read = resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
                {
                    var floats = new float[read / 4];
                    Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
                    samples16k.AddRange(floats);
                }
            }
            else
            {
                samples16k.AddRange(mono);
            }
        };

        capture.StartRecording();
        Console.WriteLine("Recording 6 seconds (inject TTS audio now)...");
        await Task.Delay(6000);
        capture.StopRecording();
        capture.Dispose();
        Console.WriteLine($"Captured {samples16k.Count} samples @ 16k = {samples16k.Count / 16000.0:F2}s");

        using var session = new ParakeetSession(modelDir);
        var t = session.Transcribe(samples16k.ToArray());
        Console.WriteLine($"DirectML={session.UsingDirectML}");
        Console.WriteLine($"TRANSCRIPT: '{t.Text}'");
    }

    static float[] Downmix(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var mono = new float[interleaved.Length / channels];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }
}

sealed class WaveFormatRawSource : WaveStream
{
    private readonly MemoryStream _ms;
    private readonly WaveFormat _fmt;
    public WaveFormatRawSource(float[] floats, int sampleRate)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        _ms = new MemoryStream(bytes);
        _fmt = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
    }
    public override WaveFormat WaveFormat => _fmt;
    public override long Length => _ms.Length;
    public override long Position { get => _ms.Position; set => _ms.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => _ms.Read(buffer, offset, count);
}
