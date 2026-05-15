#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Winpepper.App.Audio;
using Winpepper.App.Threading;
using Winpepper.App.Tray;
using Winpepper.App.Views;
using Winpepper.Core.Logging;
using Winpepper.Core.Sessions;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Winpepper.Platform.Autostart;
using Winpepper.Platform.Hotkeys;

namespace Winpepper.App.Hosting;

public sealed class AppShell : IDisposable
{
    public ILoggerFactory LogFactory { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public DebouncedSettingsWriter SettingsWriter { get; }
    public SessionEngine Engine { get; }
    public SessionViewModel SessionVm { get; }
    public RecordingSettingsViewModel RecordingVm { get; }
    public CleanupSettingsViewModel CleanupVm { get; }
    public CorrectionsViewModel CorrectionsVm { get; }
    public IAutostartRegistry Autostart { get; }
    public PipelineHost Pipeline { get; }
    public TrayIconHost Tray { get; }
    public StatusPillWindow Pill { get; }
    public MainWindow Main { get; private set; }

    private readonly WinUiSoundEffectPlayer _sounds;

    public static async Task<AppShell> BootstrapAsync(Application _)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var factory = WinpepperLogging.Create(AppPaths.LogsDir, debugConsole: false, minimumLevel: LogLevel.Information);
        var store = new SettingsStore(AppPaths.SettingsJson);
        var settings = store.Load();
        var writer = new DebouncedSettingsWriter(store);

        var uiThread = new DispatcherQueueUiThread(DispatcherQueue.GetForCurrentThread());
        var engine = new SessionEngine();
        var sessionVm = new SessionViewModel(engine, uiThread);
        var hotkeyValidator = new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator();
        var recordingVm = new RecordingSettingsViewModel(settings, writer, hotkeyValidator);
        var cleanupContract = CleanupSettingsContract.Defaults();
        var cleanupVm = new CleanupSettingsViewModel(cleanupContract, _ => { /* Plan 2 wires real persistence */ });

        // Plan 2 normally provides initial corrections; until then, empty.
        var correctionsVm = new CorrectionsViewModel(
            Array.Empty<string>(),
            new Dictionary<string, string>(),
            (_, _) => { /* Plan 2 wires CorrectionStore.Save() here */ });

        var autostart = new AutostartRegistry();
        var sounds = new WinUiSoundEffectPlayer(AppPaths.AssetsDir) { Enabled = settings.PlaySounds };

        var hold   = HotkeyChord.Parse(settings.HoldHotkey);
        var toggle = HotkeyChord.Parse(settings.ToggleHotkey);
        var cancel = HotkeyChord.Parse("Esc");
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir);

        var shell = new AppShell(factory, store, settings, writer, engine, sessionVm,
                                  recordingVm, cleanupVm, correctionsVm,
                                  autostart, pipeline, sounds);
        await shell.StartAsync();
        return shell;
    }

    private AppShell(ILoggerFactory factory, SettingsStore store, AppSettings settings,
                     DebouncedSettingsWriter writer, SessionEngine engine,
                     SessionViewModel sessionVm,
                     RecordingSettingsViewModel recVm, CleanupSettingsViewModel cleanupVm,
                     CorrectionsViewModel corrVm, IAutostartRegistry autostart,
                     PipelineHost pipeline, WinUiSoundEffectPlayer sounds)
    {
        LogFactory = factory; SettingsStore = store; Settings = settings;
        SettingsWriter = writer; Engine = engine; SessionVm = sessionVm; RecordingVm = recVm;
        CleanupVm = cleanupVm; CorrectionsVm = corrVm; Autostart = autostart;
        Pipeline = pipeline; _sounds = sounds;

        Pill = new StatusPillWindow(sessionVm);
        Tray = new TrayIconHost(sessionVm, AppPaths.AssetsDir, "0.3.0",
                                 openSettings: ShowMain, quit: Quit);
        Main = new MainWindow(this);
    }

    private async Task StartAsync()
    {
        Pipeline.Start();
        await Task.CompletedTask;
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
    }

    public void ShowMain() => ShowMain(navigateToOnboarding: false);

    public void ShowMain(bool navigateToOnboarding)
    {
        if (Main is null || Main.AppWindow is null) Main = new MainWindow(this);
        Main.Activate();
        if (navigateToOnboarding) Main.NavigateToOnboarding();
    }

    public void Quit()
    {
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        Tray.Dispose();
        Pill.Close();
        SettingsWriter.Dispose();
        _sounds.Dispose();
        WinpepperLogging.Flush();
    }
}
#endif
