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
///
/// Fail-closed behavior: archiving is refused or degraded rather than writing to a
/// place we cannot account for later — a reparse-point root, a reparse-point day
/// directory (degrades to text-only), or a present-but-corrupt/unreadable index (the
/// store refuses to append; the archive is skipped). Every skip is reported through
/// the optional <c>onArchiveSkipped</c> callback; callers that ignore the return
/// value still get an observable signal.
/// </summary>
public sealed class HistoryArchiver
{
    private const int SampleRate = 16000;

    private readonly HistoryStore _store;
    private readonly Func<DateTime> _nowUtc;
    private readonly Func<bool> _storeAudio;
    private readonly Action<string>? _onArchiveSkipped;

    public HistoryArchiver(
        HistoryStore store,
        Func<DateTime>? nowUtc = null,
        Func<bool>? storeAudio = null,
        Action<string>? onArchiveSkipped = null)
    {
        _store = store;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
        _storeAudio = storeAudio ?? (() => true);
        _onArchiveSkipped = onArchiveSkipped;
    }

    public HistoryEntry? Archive(HistoryArchiveInput input)
    {
        // Fail closed: never write WAV or index through a reparse-point root.
        if (_store.RootIsUnsafe)
        {
            Skip("History archive skipped: the history root is a junction/symlink; " +
                 "refusing to write through it.");
            return null;
        }

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
            HistoryEntry? result = null;
            _store.WithExclusiveLock(() =>
            {
                // Fail closed against a pre-planted reparse-point day directory: write
                // the text-only entry instead of following the link outside the root.
                if (DirectoryIsReparsePoint(Path.GetDirectoryName(absolute)!))
                {
                    entry = entry with { WavRelativePath = "" };
                    Skip("History audio degraded to text-only: the day directory is a " +
                         "junction/symlink; refusing to write the WAV through it.");
                    if (!TryAppend(entry)) return;
                    result = entry;
                    return;
                }
                // Validate BEFORE writing: a present-but-unreadable/corrupt index means
                // this append will be refused — never create the WAV in the first place.
                if (!_store.IndexIsWritableNow())
                {
                    Skip("History archive skipped: the history index is unreadable or corrupt; " +
                         "the new dictation was not recorded rather than overwrite existing history.");
                    return;
                }
                WavWriter.WriteMono16kInt16(absolute, input.Samples16k);
                if (!TryAppend(entry))
                {
                    // Race-only residual (index became unreadable between the probe and the
                    // append): delete the orphan WAV, and keep it OBSERVABLE if that fails.
                    if (!TryDeleteOrphanWav(absolute))
                        Skip("History archive skipped after the index refused the entry, and " +
                             "the orphaned recording could not be deleted; the file remains on " +
                             "disk unindexed.");
                    return;
                }
                result = entry;
            });
            return result;
        }

        if (!TryAppend(entry)) return null;
        return entry;
    }

    /// <summary>
    /// Append the entry; when the store refuses (present-but-unreadable/corrupt index),
    /// report and return false so the caller can skip instead of overwriting history.
    /// </summary>
    private bool TryAppend(HistoryEntry entry)
    {
        try
        {
            _store.Append(entry);
            return true;
        }
        catch (InvalidOperationException)
        {
            Skip("History archive skipped: the history index is unreadable or corrupt; " +
                 "the new dictation was not recorded rather than overwrite existing history.");
            return false;
        }
    }

    private static bool TryDeleteOrphanWav(string absolute)
    {
        try
        {
            if (File.Exists(absolute)) File.Delete(absolute);
            return !File.Exists(absolute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Skip(string reason) => _onArchiveSkipped?.Invoke(reason);

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
