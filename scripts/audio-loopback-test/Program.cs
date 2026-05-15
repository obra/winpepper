// Audio loopback verification.
// Usage:
//   dotnet run                # 5-second capture, prints RMS + writes captured.wav
//   dotnet run -- asr         # 6-second capture, runs Parakeet, prints transcript
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Length > 0 && args[0] == "asr")
{
    await Asr.Run();
    return;
}

var enumerator = new MMDeviceEnumerator();
var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
Console.WriteLine($"Default capture device: {device.FriendlyName}");

var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
Console.WriteLine($"WaveFormat: {capture.WaveFormat}");

var samples = new List<float>();
capture.DataAvailable += (s, e) =>
{
    var fmt = capture.WaveFormat;
    int sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
    if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
    {
        var floats = new float[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, floats, 0, e.BytesRecorded);
        samples.AddRange(floats);
    }
    else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
    {
        for (var i = 0; i < sampleCount; i++)
        {
            short v = BitConverter.ToInt16(e.Buffer, i * 2);
            samples.Add(v / 32768f);
        }
    }
};

capture.StartRecording();
Console.WriteLine("Recording 5 seconds...");
await Task.Delay(5000);
capture.StopRecording();
capture.Dispose();

Console.WriteLine($"Captured {samples.Count} samples ({samples.Count / (double)capture.WaveFormat.SampleRate / capture.WaveFormat.Channels:F2}s)");

if (samples.Count > 0)
{
    double sum = 0;
    float peak = 0;
    foreach (var s in samples)
    {
        sum += s * s;
        var a = Math.Abs(s);
        if (a > peak) peak = a;
    }
    var rms = Math.Sqrt(sum / samples.Count);
    Console.WriteLine($"RMS={rms:F6} peak={peak:F6}");
    Console.WriteLine(rms > 0.001 ? "SIGNAL DETECTED" : "SILENCE");
}
