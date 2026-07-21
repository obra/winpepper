using Microsoft.UI.Xaml;
using Winpepper.App.Hosting;

namespace Winpepper.App;

public partial class App : Application
{
    public static AppShell? Shell { get; private set; }
    public static Winpepper.Core.Crash.CrashHandler? CrashHandler { get; set; }

    public App()
    {
        InitializeComponent();
        // Last-chance diagnostics: XAML-layer exceptions (e.g. during page
        // realization) otherwise surface only as an opaque 0xc000027b stowed
        // exception in the Windows event log with no managed stack. Write the
        // full exception to a plain file before the process dies.
        UnhandledException += (_, e) =>
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "winpepper", "logs");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "unhandled.txt"),
                    $"[{DateTimeOffset.Now:O}] {e.Message}\n{e.Exception}\n\n");
            }
            catch
            {
                // Never mask the original failure with a logging failure.
            }
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Shell = await AppShell.BootstrapAsync(this);
    }
}
