using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.History.Diff;

namespace Winpepper.History.ViewModels;

public sealed class RerunPanelViewModel : INotifyPropertyChanged
{
    public string SelectedModelName { get; set; } = "";

    /// <summary>
    /// Absolute path to the model on disk. For ASR (Parakeet) this is the
    /// model directory; for cleanup (GGUF) this is the model directory too —
    /// the path-to-the-file variant lives in <see cref="SelectedModelPath"/>.
    /// </summary>
    public string SelectedModelDirectory { get; set; } = "";

    /// <summary>
    /// Absolute path to a single model file (used by cleanup GGUFs which
    /// <c>LlamaCleanupBackend</c> opens by file path, not by directory).
    /// </summary>
    public string SelectedModelPath { get; set; } = "";

    /// <summary>
    /// Prompt format id of the selected model (ModelDescriptor.PromptFormat).
    /// Only meaningful on the cleanup panel; defaults to chatml, the format of
    /// the registry-default cleanup model.
    /// </summary>
    public string SelectedModelPromptFormat { get; set; } = "chatml";

    /// <summary>
    /// From ModelDescriptor.OmitPromptExample: whether the selected cleanup
    /// model needs the example-free default base prompt (models that echo the
    /// worked example instead of cleaning). Mirrors the production pipeline.
    /// </summary>
    public bool SelectedModelOmitPromptExample { get; set; }

    private string _rerunText = "";
    public string RerunText
    {
        get => _rerunText;
        private set { _rerunText = value; OnPropertyChanged(); OnPropertyChanged(nameof(Diff)); }
    }

    public IReadOnlyList<WordDiffSegment> Diff { get; private set; } = Array.Empty<WordDiffSegment>();

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnPropertyChanged(); }
    }

    public Func<CancellationToken, Task<string>> Runner { get; init; } = _ => Task.FromResult("");
    public string Baseline { get; init; } = "";

    public async Task RunAsync(CancellationToken ct)
    {
        IsRunning = true;
        try
        {
            var text = await Runner(ct).ConfigureAwait(false);
            RerunText = text;
            Diff = WordDiff.Compute(Baseline, text);
            OnPropertyChanged(nameof(Diff));
        }
        finally
        {
            IsRunning = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
