namespace Winpepper.History;

/// <summary>
/// Per-stage timings (ms) captured by the session pipeline. Surfaced in the Lab.
/// </summary>
public sealed record HistoryTimings
{
    public int RecordMs { get; init; }
    public int TranscribeMs { get; init; }
    public int CleanupMs { get; init; }
    public int InjectMs { get; init; }
    public int TotalMs { get; init; }
}

/// <summary>
/// One archived dictation session. The WAV file lives at
/// <c>%LOCALAPPDATA%\winpepper\history\{WavRelativePath}</c>.
///
/// Records are immutable. The Lab rerun panels never write back into the
/// entry — promotions go through the settings store and the corrections store.
/// </summary>
public sealed record HistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public string RawTranscript { get; init; } = "";
    public string CleanedText { get; init; } = "";
    public string WavRelativePath { get; init; } = "";
    public int DurationMs { get; init; }
    public string AsrModelName { get; init; } = "";
    public string CleanupModelName { get; init; } = "";
    public bool WindowContextUsed { get; init; }
    public string WindowTitleAtStart { get; init; } = "";
    public string WindowTitleAtInject { get; init; } = "";
    public HistoryTimings Timings { get; init; } = new();

    /// <summary>80-char preview of the raw transcript, with leading/trailing whitespace trimmed.</summary>
    public string TranscriptPreview
    {
        get
        {
            var t = RawTranscript.Trim();
            return t.Length <= 80 ? t : t[..80];
        }
    }
}
