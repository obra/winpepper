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

        // Transcribe worker: hosts the native transcribe.cpp engine for the
        // parent app so a wedged native call is killable and the engine
        // restartable. Must run BEFORE WinRT/WinUI init: it is a plain
        // console loop over stdin/stdout. The parent supplies runtime/model
        // paths via the Load request; stderr carries worker logs.
        if (args.Any(a => a.Equals("--transcribe-worker", StringComparison.OrdinalIgnoreCase)))
        {
            // Suppress WER UI: a native crash must exit the worker promptly
            // (parent sees EOF -> kill/respawn) instead of wedging invisibly
            // on an error dialog.
            _ = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

            // Main is [STAThread]; the worker's blocking native loop must not
            // inherit an STA — run it on a dedicated MTA foreground thread
            // and join (SetApartmentState is a no-op where unsupported).
            var exitCode = 0;
            var loop = new Thread(() =>
            {
                exitCode = Winpepper.Asr.TranscribeCpp.Worker.TranscribeWorkerLoop.Run(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    (runtimeDir, ggufPath) => Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                        runtimeDir, ggufPath, msg => Console.Error.WriteLine($"[transcribe-worker] {msg}")),
                    msg => Console.Error.WriteLine($"[transcribe-worker] {msg}"));
            }) { IsBackground = false };
            loop.SetApartmentState(System.Threading.ApartmentState.MTA);
            loop.Start();
            loop.Join();
            return exitCode;
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

        // We are the primary instance. Second launches redirect their
        // activation here (see above); surface the main window so relaunching
        // the app is a reliable way to get the UI back (issue #10).
        instance.Activated += OnActivationRedirected;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }

    private static void OnActivationRedirected(object? sender, AppActivationArguments e)
    {
        // Raised on a non-UI thread; hop to the dispatcher via the shell.
        var shell = App.Shell;
        shell?.Ui.Post(() => shell.ShowMain());
    }

    private static void OnAppDomainUnhandled(object sender, System.UnhandledExceptionEventArgs e)
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

    // Worker-verb WER suppression: without it a native AV in transcribe.dll
    // can pop an (invisible, CreateNoWindow) WER dialog and wedge the worker
    // instead of exiting so the parent supervises it.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
}
