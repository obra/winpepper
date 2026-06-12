using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Models.ViewModels;

public sealed class ModelCardViewModel : INotifyPropertyChanged
{
    private readonly string _installRoot;
    private readonly Action<string> _promote;
    private readonly Action<Action> _dispatch;

    public ModelCardViewModel(ModelKind kind, IEnumerable<ModelDescriptor> available,
                              string installRoot, string selectedName, Action<string> promote,
                              Action<Action>? dispatch = null)
    {
        Kind = kind;
        Available = new ObservableCollection<ModelDescriptor>(available);
        _installRoot = installRoot;
        _selectedName = selectedName;
        _promote = promote;
        // ObservableCollection/PropertyChanged feed XAML bindings, so all
        // mutations must run on the UI thread. The host passes a UI-thread
        // dispatcher; tests and headless callers get inline execution.
        _dispatch = dispatch ?? (a => a());
    }

    public ModelKind Kind { get; }
    public ObservableCollection<ModelDescriptor> Available { get; }

    private string _selectedName;
    public string SelectedName
    {
        get => _selectedName;
        set { _selectedName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSelectedInstalled)); }
    }

    public ModelDescriptor? SelectedDescriptor =>
        Available.FirstOrDefault(d => d.Name == SelectedName);

    public bool IsSelectedInstalled =>
        SelectedDescriptor?.IsFullyInstalled(_installRoot) ?? false;

    public ObservableCollection<DownloadProgress> ProgressByFile { get; } = new();

    public void ReportProgress(DownloadProgress progress)
        => _dispatch(() => ReportProgressCore(progress));

    private void ReportProgressCore(DownloadProgress progress)
    {
        for (var i = 0; i < ProgressByFile.Count; i++)
        {
            if (ProgressByFile[i].DescriptorName == progress.DescriptorName
                && ProgressByFile[i].FileRelativePath == progress.FileRelativePath)
            {
                ProgressByFile[i] = progress;
                return;
            }
        }
        ProgressByFile.Add(progress);
    }

    public void CommitSelection() => _promote(SelectedName);

    /// <summary>
    /// Raise <see cref="INotifyPropertyChanged.PropertyChanged"/> for
    /// <see cref="IsSelectedInstalled"/>. Call after a download finishes so
    /// the UI re-reads the derived "yes/no" label without round-tripping
    /// through <see cref="SelectedName"/>.
    /// </summary>
    public void RaiseIsSelectedInstalledChanged()
        => _dispatch(() => OnPropertyChanged(nameof(IsSelectedInstalled)));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
