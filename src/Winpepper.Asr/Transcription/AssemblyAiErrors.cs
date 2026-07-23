namespace Winpepper.Asr.Transcription;

/// <summary>Classifies terminal AssemblyAI errors so config problems surface persistently.</summary>
public static class AssemblyAiErrors
{
    // 400 bodies for a bad model id mention the model or speech_model field.
    private static readonly string[] ModelHints = { "speech_model", "model", "unsupported model", "invalid model" };

    public static bool IsInvalidModel(AssemblyAiException ex)
    {
        if (ex.StatusCode != 400) return false;
        var m = ex.Message ?? "";
        return ModelHints.Any(h => m.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
