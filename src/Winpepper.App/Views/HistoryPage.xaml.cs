#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Winpepper.History.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryListViewModel ViewModel { get; private set; } = null!;

    public HistoryPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var services = App.Shell!.HistoryServices;
        ViewModel = new HistoryListViewModel(services.Store);
        ViewModel.Refresh();
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
