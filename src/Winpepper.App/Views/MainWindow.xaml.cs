#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Winpepper.App.Hosting;

namespace Winpepper.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;
    public MainWindow(AppShell shell)
    {
        _shell = shell;
        InitializeComponent();
        Title = "Winpepper";
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void NavigateToOnboarding()
    {
        ContentFrame.Navigate(typeof(OnboardingPage), _shell);
    }

    public void NavigateToTag(string tag)
    {
        foreach (var item in Nav.MenuItems)
        {
            if (item is NavigationViewItem navItem && (string?)navItem.Tag == tag)
            {
                Nav.SelectedItem = navItem;
                return;
            }
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var pageType = (string?)item.Tag switch
        {
            "recording"   => typeof(RecordingPage),
            "cleanup"     => typeof(CleanupPage),
            "corrections" => typeof(CorrectionsPage),
            "history"     => typeof(HistoryPage),
            "lab"         => typeof(HistoryDetailPage),
            "models"      => typeof(ModelsPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => null,
        };
        if (pageType is not null)
            ContentFrame.Navigate(pageType, _shell);
    }

    private async void OnAboutClick(object sender, TappedRoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Winpepper.Core.AboutText.Title,
            Content = Winpepper.Core.AboutText.Body(),
            CloseButtonText = "Close",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }
}
#endif
