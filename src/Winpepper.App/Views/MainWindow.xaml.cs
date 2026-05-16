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
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void NavigateToOnboarding()
    {
        ContentFrame.Navigate(typeof(OnboardingPage), _shell);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var pageType = (string?)item.Tag switch
        {
            "recording"   => typeof(RecordingPage),
            "cleanup"     => typeof(CleanupPage),
            "corrections" => typeof(CorrectionsPage),
            _ => null,
        };
        if (pageType is not null)
            ContentFrame.Navigate(pageType, _shell);
    }
}
#endif
