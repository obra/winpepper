using Microsoft.UI.Xaml;
using Winpepper.App.Hosting;
using Winpepper.Core.Hosting;

namespace Winpepper.App;

public partial class App : Application
{
    public static AppShell? Shell { get; private set; }
    public static Winpepper.Core.Crash.CrashHandler? CrashHandler { get; set; }

    public App() { InitializeComponent(); }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var shell = AppShell.Create();
        await PublishedStartup.RunAsync(
            shell,
            value => Shell = value,
            value => value.StartAsync());
    }
}
