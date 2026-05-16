using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Winpepper.App;

namespace Winpepper.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
}
