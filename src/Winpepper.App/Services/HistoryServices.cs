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
    public HistoryServices(string historyRoot, ITranscriptionRerunService transcriptionRerun)
    {
        Store = new HistoryStore(historyRoot);
        Archiver = new HistoryArchiver(Store);
        TranscriptionRerun = transcriptionRerun;
        CleanupRerun = new LlamaCleanupRerunService();
        HistoryRoot = historyRoot;
    }

    public string HistoryRoot { get; }
    public HistoryStore Store { get; }
    public HistoryArchiver Archiver { get; }
    public ITranscriptionRerunService TranscriptionRerun { get; }
    public ICleanupRerunService CleanupRerun { get; }
}
#endif
