namespace Winpepper.History;

/// <summary>
/// Bundle of session-finalize information handed to <see cref="HistoryArchiver.Archive"/>.
/// </summary>
public sealed class HistoryArchiveInput
{
    public required float[] Samples16k { get; init; }
    public string RawTranscript { get; init; } = "";
    public string CleanedText { get; init; } = "";
    public string AsrModelName { get; init; } = "";
    public string CleanupModelName { get; init; } = "";
    public bool WindowContextUsed { get; init; }
    public string WindowTitleAtStart { get; init; } = "";
    public string WindowTitleAtInject { get; init; } = "";
    public HistoryTimings Timings { get; init; } = new();
    public bool IsSilentDrop { get; init; }
}

/// <summary>
/// Session-finalize sink. When audio storage is enabled, writes the WAV under
/// <c>history-root/YYYY-MM-DD/uuid.wav</c> (UTC date), builds a
/// <see cref="HistoryEntry"/>, and appends it to the store. When audio storage is
/// disabled, normal dictations are stored as text-only entries and silent drops
/// are skipped. Retention-policy pruning happens inside <see cref="HistoryStore.Append"/>.
/// </summary>
public sealed class HistoryArchiver
{
    private const int SampleRate = 16000;

    private readonly HistoryStore _store;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<bool> _storeAudio;

    public HistoryArchiver(
        HistoryStore store,
        Func<DateTime>? nowUtc = null,
        Func<bool>? storeAudio = null)
    {
        _store = store;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _storeAudio = storeAudio ?? (() => true);
    }

    public HistoryEntry? Archive(HistoryArchiveInput input)
    {
        // Fail closed: never write WAV or index through a reparse-point root.
        if (_store.RootIsUnsafe) return null;

        var keepAudio = _storeAudio();
        if (!keepAudio && input.IsSilentDrop) return null;

        var now = _nowUtc();
        var id = Guid.NewGuid().ToString("N");
        var day = now.ToString("yyyy-MM-dd");
        var relative = keepAudio ? $"{day}/{id}.wav" : "";

        var entry = new HistoryEntry
        {
            Id = id,
            CreatedAtUtc = now,
            RawTranscript = input.RawTranscript,
            CleanedText = input.CleanedText,
            WavRelativePath = relative,
            DurationMs = (int)((long)input.Samples16k.Length * 1000 / SampleRate),
            AsrModelName = input.AsrModelName,
            CleanupModelName = input.CleanupModelName,
            WindowContextUsed = input.WindowContextUsed,
            WindowTitleAtStart = input.WindowTitleAtStart,
            WindowTitleAtInject = input.WindowTitleAtInject,
            Timings = input.Timings,
        };

        if (keepAudio)
        {
            var absolute = Path.Combine(_store.Root, relative);
            _store.WithExclusiveLock(() =>
            {
                // Fail closed against a pre-planted reparse-point day directory: write
                // the text-only entry instead of following the link outside the root.
                if (DirectoryIsReparsePoint(Path.GetDirectoryName(absolute)!))
                {
                    entry = entry with { WavRelativePath = "" };
                    _store.Append(entry);
                    return;
                }
                WavWriter.WriteMono16kInt16(absolute, input.Samples16k);
                _store.Append(entry);
            });
        }
        else
        {
            _store.Append(entry);
        }

        return entry;
    }

    private static bool DirectoryIsReparsePoint(string directory)
    {
        try
        {
            return Directory.Exists(directory) &&
                   (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
