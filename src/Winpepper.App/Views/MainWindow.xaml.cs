#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        // Mica gives the shell the standard Windows 11 layered backdrop. It
        // can fail on older systems (no DWM/composition support), in which
        // case the default opaque backdrop is kept.
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); }
        catch { /* Mica unsupported on this system; keep the default backdrop */ }

        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
        AppWindow.Closing += OnAppWindowClosing;
    }

    // Closing the window must never strand the app (issue #10): hide to the
    // tray only when the tray icon actually registered; otherwise exit
    // outright so the user is never left with a windowless, tray-less process.
    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_shell.Tray.IsRegistered) sender.Hide();
        else _shell.Quit();
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

    // Footer items like About have SelectsOnInvoked=False, so they never reach
    // OnNavSelectionChanged. ItemInvoked fires for any activation method —
    // mouse, touch, keyboard Enter, and UIA InvokePattern — so it's the right
    // place to handle them.
    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item) return;
        if ((string?)item.Tag != "about") return;

        var dialog = new ContentDialog
        {
            Title = Winpepper.Core.AboutText.Title,
            Content = Winpepper.Core.AboutText.Body(),
            CloseButtonText = "Close",
            XamlRoot = this.Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
#endif
