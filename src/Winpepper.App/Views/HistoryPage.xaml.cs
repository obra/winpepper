#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Winpepper.History.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class HistoryPage : Page
{
    private HistoryRetentionViewModel? _retentionViewModel;
    private bool _updatingRetentionControls;

    public HistoryListViewModel ViewModel { get; private set; } = null!;

    public HistoryPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var shell = App.Shell!;
        var services = shell.HistoryServices;
        ViewModel = new HistoryListViewModel(services.Store);
        ViewModel.Refresh();

        if (_retentionViewModel is not null)
            _retentionViewModel.RetentionApplied -= OnRetentionApplied;

        var retentionViewModel = new HistoryRetentionViewModel(
            services.Store,
            shell.SettingsWriter,
            services.RetentionSlot);
        _retentionViewModel = retentionViewModel;

        _updatingRetentionControls = true;
        try
        {
            StoreAudioToggle.IsOn = retentionViewModel.StoreAudioEnabled;
            MaxEntriesBox.Value = retentionViewModel.MaxEntries;
            MaxAgeBox.Value = retentionViewModel.MaxAgeDays;
            KeepForeverCheck.IsChecked = retentionViewModel.KeepForever;
            MaxAgeBox.IsEnabled = !retentionViewModel.KeepForever;
            DiskUsageText.Text = retentionViewModel.DiskUsageDisplay;
            RetentionStatusText.Text = "";
        }
        finally
        {
            _updatingRetentionControls = false;
        }

        retentionViewModel.RetentionApplied += OnRetentionApplied;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (_retentionViewModel is not null)
            _retentionViewModel.RetentionApplied -= OnRetentionApplied;

        base.OnNavigatedFrom(e);
    }

    private void OnStoreAudioToggled(object sender, RoutedEventArgs e)
    {
        if (_updatingRetentionControls || _retentionViewModel is null) return;
        _retentionViewModel.StoreAudioEnabled = StoreAudioToggle.IsOn;
    }

    private void OnMaxEntriesChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_updatingRetentionControls || _retentionViewModel is null ||
            double.IsNaN(e.NewValue)) return;

        _retentionViewModel.MaxEntries = e.NewValue;
    }

    private void OnMaxAgeChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_updatingRetentionControls || _retentionViewModel is null ||
            double.IsNaN(e.NewValue)) return;

        _retentionViewModel.MaxAgeDays = e.NewValue;
    }

    private void OnKeepForeverChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingRetentionControls || _retentionViewModel is null) return;

        _retentionViewModel.KeepForever = KeepForeverCheck.IsChecked == true;
        MaxAgeBox.IsEnabled = !_retentionViewModel.KeepForever;
    }

    private void OnRetentionApplied(object? sender, EventArgs e)
    {
        if (_retentionViewModel is not { } retentionViewModel) return;

        ViewModel.Refresh();
        DiskUsageText.Text = retentionViewModel.DiskUsageDisplay;
        MaxAgeBox.IsEnabled = !retentionViewModel.KeepForever;

        var status = "";
        if (!retentionViewModel.LastCommitPersisted)
            status = "Setting could not be saved right now; it will be retried.";
        if (retentionViewModel.LastApplyHadIndexFailure)
        {
            const string indexFailure =
                "The history index could not be updated; retry to finish applying the limit.";
            status = string.IsNullOrEmpty(status) ? indexFailure : $"{status} {indexFailure}";
        }

        RetentionStatusText.Text = status;
    }

    private async void OnDeleteAllAudio(object sender, RoutedEventArgs e)
    {
        if (_retentionViewModel is not { } retentionViewModel) return;

        var dialog = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Delete all saved audio?",
            Content = "Recordings are deleted; transcripts are kept. This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await retentionViewModel.DeleteAllAudioAsync();
        var status = $"{result.DeletedCount} recordings deleted.";
        if (result.FailedCount > 0)
        {
            status += $" {result.FailedCount} could not be deleted (file in use) — " +
                      "press again to retry.";
        }
        if (result.EnumerationFailed)
        {
            status += " Part of the history folder could not be scanned; " +
                      "the result above is incomplete.";
        }
        if (result.IndexSaveFailed)
        {
            status += " The history index could not be updated; your entry list may " +
                      "still show audio paths until the next cleanup.";
        }

        RetentionStatusText.Text = status;
    }

    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryRowViewModel row)
        {
            Frame.Navigate(typeof(HistoryDetailPage), row.Entry);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryRowViewModel row })
        {
            var pkg = new DataPackage();
            pkg.SetText(row.Entry.CleanedText);
            Clipboard.SetContent(pkg);
        }
    }
}
#endif
