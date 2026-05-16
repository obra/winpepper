using Microsoft.UI.Xaml;
using Winpepper.App.Hosting;

namespace Winpepper.App;

public partial class App : Application
{
    public static AppShell? Shell { get; private set; }

    public App() { InitializeComponent(); }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Shell = await AppShell.BootstrapAsync(this);
    }
}
