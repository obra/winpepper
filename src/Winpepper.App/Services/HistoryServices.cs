#if WINDOWS
using Winpepper.History;
using Winpepper.History.Lab;

namespace Winpepper.App.Services;

/// <summary>
/// Singleton bag of history-related services. Pages reach these via
/// <c>App.Shell.HistoryServices</c>.
/// </summary>
public sealed class HistoryServices
{
    public HistoryServices(
        string historyRoot,
        ITranscriptionRerunService transcriptionRerun,
        Func<Winpepper.Core.Settings.AppSettings> settingsProvider,
        Action<string>? onArchiveSkipped = null)
    {
        RetentionSlot = PublishedHistoryRetentionSlot.FromSettings(settingsProvider());
        Store = new HistoryStore(historyRoot, () => RetentionSlot.Policy);
        Archiver = new HistoryArchiver(
            Store,
            storeAudio: () => RetentionSlot.StoreAudio,
            onArchiveSkipped: onArchiveSkipped);
        TranscriptionRerun = transcriptionRerun;
        CleanupRerun = new LlamaCleanupRerunService();
        HistoryRoot = historyRoot;
    }

    public string HistoryRoot { get; }
    public PublishedHistoryRetentionSlot RetentionSlot { get; }
    public HistoryStore Store { get; }
    public HistoryArchiver Archiver { get; }
    public ITranscriptionRerunService TranscriptionRerun { get; }
    public ICleanupRerunService CleanupRerun { get; }
}
#endif
