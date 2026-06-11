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
    public Winpepper.App.Services.HistoryServices HistoryServices { get; }
    public Winpepper.App.Services.ModelsServices ModelsServices { get; }
    public Winpepper.Core.Errors.ErrorBus ErrorBus { get; }
    public Winpepper.Core.Notifications.IToastService Toasts { get; }
    public Winpepper.Platform.Injection.ClipboardFallback ClipboardFallback { get; }
    public Winpepper.Core.Crash.CrashHandler CrashHandler { get; }
    public Winpepper.Core.Logging.LogRingBuffer LogTail { get; }
    public Winpepper.Core.Threading.IUiThread Ui { get; }
    public Winpepper.App.Hosting.DiagnosticsHost DiagnosticsHost { get; }

    private readonly WinUiSoundEffectPlayer _sounds;

    public static async Task<AppShell> BootstrapAsync(Application app)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var logTail = new Winpepper.Core.Logging.LogRingBuffer(capacity: 2000);
        var factory = Winpepper.Core.Logging.WinpepperLogging.CreateWithBuffer(
            AppPaths.LogsDir, debugConsole: false,
            minimumLevel: LogLevel.Information,
            buffer: logTail);
        var store = new SettingsStore(AppPaths.SettingsJson);
        var settings = store.Load();
        var writer = new DebouncedSettingsWriter(store);

        var uiThread = new DispatcherQueueUiThread(DispatcherQueue.GetForCurrentThread());
        var engine = new SessionEngine();
        var sessionVm = new SessionViewModel(engine, uiThread);
        var errorBus = new Winpepper.Core.Errors.ErrorBus();
        sessionVm.AttachErrorBus(errorBus);

        var toasts = new Winpepper.App.Notifications.AppNotificationToastService();
        var diagHost = new Winpepper.App.Hosting.DiagnosticsHost(
            mainWindow: () => Winpepper.App.App.Shell?.Main,
            logsDir: AppPaths.LogsDir,
            historyRoot: AppPaths.HistoryRoot,
            settingsPath: AppPaths.SettingsJson,
            appVersion: "0.5.0");

        errorBus.Subscribe(rec =>
        {
            var tag = Winpepper.Core.Errors.ErrorDeepLink.NavigationTagFor(rec.Stage);
            var label = Winpepper.Core.Errors.ErrorDeepLink.ActionLabelFor(rec.Stage);
            _ = toasts.ShowAsync(
                "Winpepper error",
                $"{rec.Stage}: {rec.Message}",
                new[] { new Winpepper.Core.Notifications.ToastButton(tag, label) },
                TimeSpan.FromSeconds(10)).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && !string.IsNullOrEmpty(t.Result))
                    {
                        uiThread.Post(() => (Winpepper.App.App.Shell?.Main as Winpepper.App.Views.MainWindow)?.NavigateToTag(t.Result));
                        uiThread.Post(() => Winpepper.App.App.Shell?.ShowMain());
                    }
                });
        });
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

        var historyServices = new Winpepper.App.Services.HistoryServices(AppPaths.HistoryRoot);
        var modelsServices = new Winpepper.App.Services.ModelsServices(Path.Combine(AppPaths.Root, "models"));
        var cleanupModelName = settings.CleanupModelName;

        var hold   = HotkeyChord.Parse(settings.HoldHotkey);
        var toggle = HotkeyChord.Parse(settings.ToggleHotkey);
        var cancel = HotkeyChord.Parse("Esc");
        var clipboard = new Winpepper.App.Hosting.WindowsClipboard();
        var clipboardFallback = new Winpepper.Platform.Injection.ClipboardFallback(clipboard);

        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null;
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null;
        try
        {
            var uiaWatcher = new Winpepper.Platform.Learning.UiaFocusedElementTextWatcher(
                factory.CreateLogger<Winpepper.Platform.Learning.UiaFocusedElementTextWatcher>());
            focusedCapturer = new Winpepper.Platform.Learning.FocusedElementCapturer(
                uiaWatcher,
                factory.CreateLogger<Winpepper.Platform.Learning.FocusedElementCapturer>());
            if (correctionStore is not null)
            {
                var corrWriter = new Winpepper.Corrections.CorrectionStoreWriter(correctionStore);
                var prompt = new Winpepper.Core.Learning.ToastPostPasteToastPrompt(toasts);
                postPaste = new Winpepper.Core.Learning.PostPasteWatcher(uiaWatcher, corrWriter, prompt);
            }
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "PostPasteWatcher unavailable; post-paste learning will be disabled.");
        }

        Directory.CreateDirectory(AppPaths.CrashesDir);
        var miniDump = new Winpepper.Platform.Crash.MiniDumpWriter(AppPaths.CrashesDir,
            factory.CreateLogger<Winpepper.Platform.Crash.MiniDumpWriter>());
        var crashHandler = new Winpepper.Core.Crash.CrashHandler(miniDump, errorBus, engine,
            factory.CreateLogger<Winpepper.Core.Crash.CrashHandler>());
        App.CrashHandler = crashHandler;

        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         historyServices.Archiver, settings.AsrModelName, cleanupModelName,
                                         clipboardFallback, toasts,
                                         cleanup, correctionStore, windowContext, cleanupOptions,
                                         postPaste: postPaste, focusedCapturer: focusedCapturer);

        var shell = new AppShell(factory, store, settings, writer, engine, sessionVm, errorBus,
                                  recordingVm, cleanupVm, correctionsVm,
                                  autostart, pipeline, sounds, historyServices, modelsServices,
                                  toasts, clipboardFallback, crashHandler,
                                  logTail, uiThread, diagHost);
        await shell.StartAsync();
        return shell;
    }

    private AppShell(ILoggerFactory factory, SettingsStore store, AppSettings settings,
                     DebouncedSettingsWriter writer, SessionEngine engine,
                     SessionViewModel sessionVm,
                     Winpepper.Core.Errors.ErrorBus errorBus,
                     RecordingSettingsViewModel recVm, CleanupSettingsViewModel cleanupVm,
                     CorrectionsViewModel corrVm, IAutostartRegistry autostart,
                     PipelineHost pipeline, WinUiSoundEffectPlayer sounds,
                     Winpepper.App.Services.HistoryServices historyServices,
                     Winpepper.App.Services.ModelsServices modelsServices,
                     Winpepper.Core.Notifications.IToastService toasts,
                     Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
                     Winpepper.Core.Crash.CrashHandler crashHandler,
                     Winpepper.Core.Logging.LogRingBuffer logTail,
                     Winpepper.Core.Threading.IUiThread ui,
                     Winpepper.App.Hosting.DiagnosticsHost diagnosticsHost)
    {
        LogFactory = factory; SettingsStore = store; Settings = settings;
        SettingsWriter = writer; Engine = engine; SessionVm = sessionVm; RecordingVm = recVm;
        CleanupVm = cleanupVm; CorrectionsVm = corrVm; Autostart = autostart;
        Pipeline = pipeline; _sounds = sounds;
        ErrorBus = errorBus;
        HistoryServices = historyServices; ModelsServices = modelsServices;
        Toasts = toasts; ClipboardFallback = clipboardFallback;
        CrashHandler = crashHandler;
        LogTail = logTail; Ui = ui; DiagnosticsHost = diagnosticsHost;

        Pill = new StatusPillWindow(sessionVm);
        Tray = new TrayIconHost(sessionVm, AppPaths.AssetsDir, "0.3.0",
                                 openSettings: ShowMain, quit: Quit);
        Main = new MainWindow(this);
    }

    private async Task StartAsync()
    {
        var startHidden = Environment.GetEnvironmentVariable("WINPEPPER_START_HIDDEN") == "1";
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
        else if (!startHidden) ShowMain(navigateToOnboarding: false);
        // else: stay tray-only (autostart with --tray).

        // Start the pipeline only after the window is up so a missing or
        // corrupt model can never block first paint (issue #6). TryStart
        // reports a missing model on the error bus and leaves the pipeline
        // disabled; the Models tab re-attempts after a download completes.
        Pipeline.TryStart();
        await Task.CompletedTask;
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
        ModelsServices.Dispose();
        (Toasts as IDisposable)?.Dispose();
        WinpepperLogging.Flush();
    }
}
#endif
