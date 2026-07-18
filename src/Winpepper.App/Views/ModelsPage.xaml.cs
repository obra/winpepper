#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class ModelsPage : Page
{
    private bool _downloadInProgress;

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
            // The progress bridge requires an observable enqueue result: if
            // navigation/app shutdown has closed this queue, fail its drain
            // instead of waiting forever for a callback that cannot run.
            dispatch: a =>
            {
                if (!DispatcherQueue.TryEnqueue(() => a()))
                    throw new InvalidOperationException("The UI dispatcher rejected model progress work.");
            });

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
        if (_downloadInProgress) return;
        _downloadInProgress = true;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;

        try
        {
            await ViewModel.DownloadMissingAsync(CancellationToken.None);
            UpdateInstalledLabels();

            // If the pipeline was left disabled at boot because models were
            // missing (issue #6), bring it up now that the download finished.
            App.Shell!.Pipeline.TryStart();
        }
        catch (OperationCanceledException)
        {
            // A future cancel button can use this path without surfacing a
            // cancellation as an application crash.
        }
        catch (Exception ex)
        {
            var shell = App.Shell!;
            shell.LogFactory.CreateLogger<ModelsPage>()
                .LogError(ex, "Model download failed");
            shell.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
        }
        finally
        {
            if (button is not null) button.IsEnabled = true;
            _downloadInProgress = false;
        }
    }

    private void UpdateInstalledLabels()
    {
        var asrInstalled = ViewModel.AsrCard.IsSelectedInstalled;
        AsrInstalledText.Text = asrInstalled ? "Installed" : "Not downloaded";
        AsrInstalledIcon.Visibility = asrInstalled ? Visibility.Visible : Visibility.Collapsed;
        AsrNotInstalledIcon.Visibility = asrInstalled ? Visibility.Collapsed : Visibility.Visible;

        var cleanupInstalled = ViewModel.CleanupCard.IsSelectedInstalled;
        CleanupInstalledText.Text = cleanupInstalled ? "Installed" : "Not downloaded";
        CleanupInstalledIcon.Visibility = cleanupInstalled ? Visibility.Visible : Visibility.Collapsed;
        CleanupNotInstalledIcon.Visibility = cleanupInstalled ? Visibility.Collapsed : Visibility.Visible;
    }
}
#endif
