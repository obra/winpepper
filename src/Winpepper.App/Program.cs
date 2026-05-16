using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Winpepper.App;
using Winpepper.Core;

namespace Winpepper.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Install smoke probe: must run BEFORE WinRT/WinUI init or any global
        // exception hookups so it's fast, minimal, and safe on bare VMs.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return SelftestProbe.Run(Console.WriteLine);
        }

        // Autostart hand-off: --tray means start hidden to the tray.
        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("WINPEPPER_START_HIDDEN", startHidden ? "1" : "0");

        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;

        // Single-instance handshake. If a sibling is already running, redirect
        // activation and exit.
        var key = "Winpepper-singleton";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent)
        {
            var current = AppInstance.GetCurrent();
            instance.RedirectActivationToAsync(current.GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }

    private static void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex) return;
        var keepAlive = App.CrashHandler?.HandleUnhandled(ex, fromTaskScheduler: false) ?? false;
        if (!keepAlive) Environment.Exit(1);
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var keepAlive = App.CrashHandler?.HandleUnhandled(e.Exception, fromTaskScheduler: true) ?? false;
        e.SetObserved();
        if (!keepAlive) Environment.Exit(1);
    }
}
