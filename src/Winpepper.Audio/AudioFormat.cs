namespace Winpepper.Audio;

public sealed record AudioFormat(int SampleRate, int Channels);

public static class WinpepperAudioFormat
{
    public static readonly AudioFormat Mono16k = new(SampleRate: 16000, Channels: 1);
}
