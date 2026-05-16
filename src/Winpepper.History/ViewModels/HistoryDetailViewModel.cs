using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.History.Lab;

namespace Winpepper.History.ViewModels;

public sealed class HistoryDetailViewModel : INotifyPropertyChanged
{
    private readonly string _historyRoot;
    private readonly ITranscriptionRerunService _transcriptionService;
    private readonly ICleanupRerunService _cleanupService;
    private readonly Action<string> _promoteAsrDefault;
    private readonly Action<string> _promoteCleanupDefault;

    public HistoryDetailViewModel(
        HistoryEntry entry,
        string historyRoot,
        ITranscriptionRerunService transcriptionService,
        ICleanupRerunService cleanupService,
        Action<string> promoteAsrDefault,
        Action<string> promoteCleanupDefault)
    {
        Entry = entry;
        _historyRoot = historyRoot;
        _transcriptionService = transcriptionService;
        _cleanupService = cleanupService;
        _promoteAsrDefault = promoteAsrDefault;
        _promoteCleanupDefault = promoteCleanupDefault;

        // Lambdas capture 'this' — suppress nullable warning: fields are set
        // before the runner is ever invoked.
        TranscriptionPanel = new RerunPanelViewModel
        {
            Baseline = entry.RawTranscript,
            Runner = async ct =>
            {
                var r = await _transcriptionService.RerunAsync(
                    WavAbsolutePath, TranscriptionPanel!.SelectedModelName,
                    TranscriptionPanel!.SelectedModelDirectory, ct);
                return r.Text;
            },
        };

        CleanupPanel = new RerunPanelViewModel
        {
            Baseline = entry.CleanedText,
            Runner = async ct =>
            {
                var r = await _cleanupService.RerunAsync(new CleanupRerunInput
                {
                    RawTranscript = entry.RawTranscript,
                    ModelName = CleanupPanel!.SelectedModelName,
                    ModelPath = CleanupPanel!.SelectedModelPath,
                    CustomBasePrompt = CleanupCustomPrompt,
                    IncludeWindowContext = IncludeWindowContextInRerun,
                    WindowContextText = "",
                    Corrections = Winpepper.Corrections.CorrectionsData.Empty,
                }, ct);
                CleanupAssembledPrompt = r.AssembledPrompt;
                CleanupRawOutput = r.RawOutput;
                return r.CleanedText;
            },
        };
    }

    public HistoryEntry Entry { get; }

    public string OriginalTranscript => Entry.RawTranscript;
    public string OriginalCleanedText => Entry.CleanedText;
    public string WavAbsolutePath => Path.Combine(_historyRoot, Entry.WavRelativePath);

    public RerunPanelViewModel TranscriptionPanel { get; }
    public RerunPanelViewModel CleanupPanel { get; }

    public string CleanupCustomPrompt { get; set; } = "";
    public bool IncludeWindowContextInRerun { get; set; }

    private string _cleanupAssembledPrompt = "";
    public string CleanupAssembledPrompt
    {
        get => _cleanupAssembledPrompt;
        private set { _cleanupAssembledPrompt = value; OnPropertyChanged(); }
    }

    private string _cleanupRawOutput = "";
    public string CleanupRawOutput
    {
        get => _cleanupRawOutput;
        private set { _cleanupRawOutput = value; OnPropertyChanged(); }
    }

    public void PromoteTranscriptionRerunAsDefault()
        => _promoteAsrDefault(TranscriptionPanel.SelectedModelName);

    public void PromoteCleanupRerunAsDefault()
        => _promoteCleanupDefault(CleanupPanel.SelectedModelName);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
