namespace Winpepper.Asr.Transcription;

public sealed class AssemblyAiOptions
{
    public string BaseUrl { get; init; } = "https://api.assemblyai.com";
    public string Model { get; init; } = "universal-2";
    public string LanguageCode { get; init; } = "en_us";
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxTransientRetries { get; init; } = 3;
}
