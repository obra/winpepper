namespace Winpepper.Asr.Transcription;

public sealed class AssemblyAiOptions
{
    public string BaseUrl { get; init; } = "https://api.assemblyai.com";
    public string StreamingBaseUrl { get; init; } = "wss://streaming.assemblyai.com";
    public string Model { get; init; } = "universal-2";
    public string LanguageCode { get; init; } = "en_us";
    // Include filler words ("um", "uh") in the transcript verbatim. Off by
    // default: dictation output should be clean. Eval reference generation
    // turns this on so local models are not penalized for transcribing fillers.
    public bool Disfluencies { get; init; } = false;

    // Single owned cloud budget. FallbackTranscriber cancels the cloud attempt
    // after CloudDeadline; the client caps each HTTP request at PerRequestTimeout
    // via a linked CTS (NOT the global HttpClient.Timeout).
    public TimeSpan CloudDeadline { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PerRequestTimeout { get; init; } = TimeSpan.FromSeconds(8);

    // Clips take at least ~750 ms to enter processing; wait before the first poll,
    // then poll at PollInterval.
    public TimeSpan FirstPollDelay { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxTransientRetries { get; init; } = 3;

    // Retention: delete the remote transcript after success.
    public bool DeleteAfterTranscribe { get; init; } = true;

    // Send Preferred terms as keyterms_prompt (paid add-on on some tiers). Off by default.
    public bool KeytermsEnabled { get; init; } = false;

    /// <summary>Clamp a user-supplied cloud-deadline seconds value to [5, 30].</summary>
    public static TimeSpan ClampDeadline(int seconds)
        => TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 30));
}
