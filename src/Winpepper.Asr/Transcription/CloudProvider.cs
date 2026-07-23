namespace Winpepper.Asr.Transcription;

/// <summary>
/// Classifies a TranscriptionResult.ProviderModelName. Cloud results (AssemblyAI)
/// are already server-side punctuated/formatted, so downstream cleanup can skip
/// the local LLM pass and run only the deterministic correction pass.
/// </summary>
public static class CloudProvider
{
    public const string AssemblyAiPrefix = "assemblyai/";

    public static bool IsCloud(string providerModelName)
        => !string.IsNullOrEmpty(providerModelName)
           && providerModelName.StartsWith(AssemblyAiPrefix, StringComparison.OrdinalIgnoreCase);
}
