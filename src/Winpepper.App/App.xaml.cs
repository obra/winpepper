using Microsoft.UI.Xaml;
using Winpepper.App.Hosting;

namespace Winpepper.App;

public partial class App : Application
{
    public static AppShell? Shell { get; private set; }
    public static Winpepper.Core.Crash.CrashHandler? CrashHandler { get; set; }

    public App() { InitializeComponent(); }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Shell = await AppShell.BootstrapAsync(this);
    }
}
