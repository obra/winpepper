#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class ModelsPage : Page
{
    public ModelsTabViewModel ViewModel { get; private set; } = null!;

    public ModelsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var models = App.Shell!.ModelsServices;
        var settings = App.Shell!.SettingsStore;
        var s = settings.Load();

        ViewModel = new ModelsTabViewModel(
            models.Registry, models.ModelsRoot, models,
            currentAsrName: s.AsrModelName,
            currentCleanupName: s.CleanupModelName,
            promoteAsr: name =>
            {
                var cur = settings.Load();
                settings.Save(cur with { AsrModelName = name });
            },
            promoteCleanup: name =>
            {
                var cur = settings.Load();
                settings.Save(cur with { CleanupModelName = name });
            },
            // Download progress callbacks arrive on ThreadPool threads (the
            // loop in DownloadMissingAsync resumes off-context after its
            // ConfigureAwait(false) awaits). XAML-bound state must only be
            // touched on the UI thread, so route mutations through it.
            dispatch: a => App.Shell!.Ui.Post(a));

        AsrCombo.SelectedItem = ViewModel.AsrCard.SelectedDescriptor;
        CleanupCombo.SelectedItem = ViewModel.CleanupCard.SelectedDescriptor;
        UpdateInstalledLabels();
    }

    private void OnAsrChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AsrCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.AsrCard.SelectedName = d.Name;
            ViewModel.AsrCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    private void OnCleanupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupCard.SelectedName = d.Name;
            ViewModel.CleanupCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    private async void OnDownloadMissing(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadMissingAsync(CancellationToken.None);
        UpdateInstalledLabels();
    }

    private void UpdateInstalledLabels()
    {
        AsrInstalledText.Text = ViewModel.AsrCard.IsSelectedInstalled ? "yes" : "no";
        CleanupInstalledText.Text = ViewModel.CleanupCard.IsSelectedInstalled ? "yes" : "no";
    }
}
#endif
