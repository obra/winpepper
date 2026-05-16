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

        // PLAN2-TYPE — Plan 2 owns these types; constructing them here so Plan 3's
        // pipeline can invoke real cleanup + window context. Each one is optional —
        // if the model or registry isn't present yet, we fall back to raw transcript.
        Winpepper.Cleanup.CleanupRunner? cleanup = null;
        Winpepper.Corrections.CorrectionStore? correctionStore = null;
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null;

        try
        {
            correctionStore = new Winpepper.Corrections.CorrectionStore(AppPaths.CorrectionsJson);
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "CorrectionStore unavailable; cleanup will run with empty corrections.");
        }

        try
        {
            // Plan 2's LlamaCleanupBackend (line 2141) is constructed with the path to
            // the .gguf file (not the directory). The cleanup model lives at
            // <Root>/models/cleanup/<name>.gguf. We pick the first .gguf in that dir.
            var cleanupModelDir = Path.Combine(AppPaths.Root, "models", "cleanup");
            var modelFile = Directory.Exists(cleanupModelDir)
                ? Directory.EnumerateFiles(cleanupModelDir, "*.gguf", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (modelFile is not null)
            {
                var backend = new Winpepper.Cleanup.LlamaCleanupBackend(modelFile,
                    factory.CreateLogger<Winpepper.Cleanup.LlamaCleanupBackend>());
                cleanup = new Winpepper.Cleanup.CleanupRunner(backend,
                    factory.CreateLogger<Winpepper.Cleanup.CleanupRunner>());
            }
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "Cleanup runner unavailable; falling back to raw transcripts.");
        }

        try
        {
            // CreateWindows is Plan 2's production factory (line 3480 of plan 2);
            // UiaTreeReader and OcrFallback both take a logger.
            windowContext = Winpepper.Platform.WindowContext.WindowContextPrefetch.CreateWindows(
                new Winpepper.Platform.WindowContext.UiaTreeReader(
                    factory.CreateLogger<Winpepper.Platform.WindowContext.UiaTreeReader>()),
                new Winpepper.Platform.WindowContext.OcrFallback(
                    factory.CreateLogger<Winpepper.Platform.WindowContext.OcrFallback>()),
                factory.CreateLogger<Winpepper.Platform.WindowContext.WindowContextPrefetch>());
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "WindowContextPrefetch unavailable; cleanup will run without window context.");
        }

        // Build CleanupOptions from current cleanup settings (Plan 3 keeps these in
        // the CleanupSettingsViewModel; here we read once at boot and re-read in
        // Plan 4's settings-reactive wiring).
        var cleanupOptions = new Winpepper.Cleanup.CleanupOptions
        {
            Profile = ParseProfile(cleanupContract.Profile),
            CustomBasePrompt = cleanupContract.CustomPrompt,
            Timeout = TimeSpan.FromMilliseconds(cleanupContract.TimeoutMs),
            WindowContextEnabled = cleanupContract.WindowContextEnabled,
            MaxNewTokensCap = cleanupContract.MaxNewTokens,
        };

        var historyStore = new Winpepper.History.HistoryStore(AppPaths.HistoryRoot);
        var archiver = new Winpepper.History.HistoryArchiver(historyStore);
        var cleanupModelName = settings.CleanupModelName;

        var hold   = HotkeyChord.Parse(settings.HoldHotkey);
        var toggle = HotkeyChord.Parse(settings.ToggleHotkey);
        var cancel = HotkeyChord.Parse("Esc");
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         archiver, settings.AsrModelName, cleanupModelName,
                                         cleanup, correctionStore, windowContext, cleanupOptions);

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
        var startHidden = Environment.GetEnvironmentVariable("WINPEPPER_START_HIDDEN") == "1";
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
        else if (!startHidden) ShowMain(navigateToOnboarding: false);
        // else: stay tray-only (autostart with --tray).
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

    private static Winpepper.Cleanup.CleanupProfile ParseProfile(string s) => s switch
    {
        "Ordinary" => Winpepper.Cleanup.CleanupProfile.Ordinary,
        "Literal"  => Winpepper.Cleanup.CleanupProfile.Literal,
        "Custom"   => Winpepper.Cleanup.CleanupProfile.Custom,
        _          => Winpepper.Cleanup.CleanupProfile.Ordinary,
    };

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
