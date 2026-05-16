#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsViewModel ViewModel { get; private set; } = null!;

    public DiagnosticsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        ViewModel = new DiagnosticsViewModel(
            shell.LogTail,
            shell.Ui,
            shell.DiagnosticsHost);
        Bindings.Update();
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => ViewModel.OpenLogFolder();

    private async void OnCopyBundle(object sender, RoutedEventArgs e)
    {
        await ViewModel.CopyDiagnosticsBundleAsync();
        LastBundleLabel.Text = string.IsNullOrEmpty(ViewModel.LastBundlePath)
            ? ""
            : $"Saved: {ViewModel.LastBundlePath}";
    }
}
#endif
