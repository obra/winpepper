#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Storage;
using Winpepper.History;
using Winpepper.History.ViewModels;
using Winpepper.Models;

namespace Winpepper.App.Views;

public sealed partial class HistoryDetailPage : Page
{
    public HistoryDetailViewModel? ViewModel { get; private set; }
    public IReadOnlyList<ModelDescriptor> AvailableAsrModels { get; private set; } = Array.Empty<ModelDescriptor>();
    public IReadOnlyList<ModelDescriptor> AvailableCleanupModels { get; private set; } = Array.Empty<ModelDescriptor>();

    public HistoryDetailPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Task 23 routes the "Lab" nav-rail click to this page with no
        // parameter; only a row click from HistoryPage hands us an entry.
        // Bail out cleanly when the parameter is missing so the empty Lab
        // shows instead of throwing.
        if (e.Parameter is not HistoryEntry entry) return;

        var history = App.Shell!.HistoryServices;
        var models = App.Shell!.ModelsServices;
        var settings = App.Shell!.SettingsStore;

        AvailableAsrModels = models.Registry.ByKind(ModelKind.Asr).ToList();
        AvailableCleanupModels = models.Registry.ByKind(ModelKind.Cleanup).ToList();

        ViewModel = new HistoryDetailViewModel(
            entry, history.HistoryRoot,
            history.TranscriptionRerun, history.CleanupRerun,
            promoteAsrDefault: name =>
            {
                var s = settings.Load();
                settings.Save(s with { AsrModelName = name });
            },
            promoteCleanupDefault: name =>
            {
                var s = settings.Load();
                settings.Save(s with { CleanupModelName = name });
            });

        OriginalTranscriptText.Text = ViewModel.OriginalTranscript;
        OriginalCleanedText.Text = ViewModel.OriginalCleanedText;

        try
        {
            var wavPath = ViewModel.WavAbsolutePath;
            var file = await StorageFile.GetFileFromPathAsync(wavPath);
            WavPlayer.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception)
        {
            WavPlayer.Source = null;
        }

        // Bind diffs.
        ViewModel.TranscriptionPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                TranscriptionDiff.Segments = ViewModel.TranscriptionPanel.Diff;
        };
        ViewModel.CleanupPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                CleanupDiff.Segments = ViewModel.CleanupPanel.Diff;
        };
    }

    private void OnAsrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (AsrModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.TranscriptionPanel.SelectedModelName = d.Name;
            var models = App.Shell!.ModelsServices;
            ViewModel.TranscriptionPanel.SelectedModelDirectory =
                Path.Combine(models.ModelsRoot, d.InstallDirRelative);
        }
    }

    private void OnCleanupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (CleanupModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupPanel.SelectedModelName = d.Name;
            var models = App.Shell!.ModelsServices;
            var dir = Path.Combine(models.ModelsRoot, d.InstallDirRelative);
            var file = d.Files.FirstOrDefault();
            ViewModel.CleanupPanel.SelectedModelDirectory = dir;
            ViewModel.CleanupPanel.SelectedModelPath =
                file is null ? "" : Path.Combine(dir, file.RelativePath);
        }
    }

    private async void OnRunTranscriptionRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TranscriptionPanel.RunAsync(CancellationToken.None);
    }

    private async void OnRunCleanupRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.CleanupCustomPrompt = CustomPromptBox.Text;
        ViewModel.IncludeWindowContextInRerun = WindowContextToggle.IsOn;
        await ViewModel.CleanupPanel.RunAsync(CancellationToken.None);
    }

    private async void OnShowCleanupTranscript(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Cleanup transcript",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = $"=== Assembled prompt ===\n{ViewModel.CleanupAssembledPrompt}\n\n=== Raw model output ===\n{ViewModel.CleanupRawOutput}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                },
                Height = 480,
            },
        };
        await dlg.ShowAsync();
    }

    private void OnPromoteAsr(object sender, RoutedEventArgs e) => ViewModel?.PromoteTranscriptionRerunAsDefault();
    private void OnPromoteCleanup(object sender, RoutedEventArgs e) => ViewModel?.PromoteCleanupRerunAsDefault();
}
#endif
