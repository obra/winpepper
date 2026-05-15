namespace Winpepper.Audio;

public interface IAudioRecorder : IDisposable
{
    AudioFormat Format { get; }
    event Action<ReadOnlyMemory<float>>? FramesAvailable;
    void Start();
    float[] Stop();
}
