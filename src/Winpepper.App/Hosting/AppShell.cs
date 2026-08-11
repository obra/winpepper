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
    public Winpepper.Asr.Transcription.IAssemblyAiKeyStore AssemblyAiKeyStore { get; }
    public Winpepper.Asr.Transcription.AssemblyAiClient AssemblyAiClient { get; }
    public Winpepper.Asr.Transcription.AssemblyAiOptions AssemblyAiOptions { get; }

    /// <summary>
    /// Thread-safe in-memory desired-ASR-model transport: promote callbacks
    /// publish the newly selected raw name here (persistence to settings.json
    /// is durability only), and PipelineHost's dictation seam reads it.
    /// </summary>
    public Winpepper.Core.Settings.AsrModelSelectionSlot AsrModelSelection { get; }

    /// <summary>
    /// Streaming (primary) analog of <see cref="AsrModelSelection"/>: promote
    /// callbacks publish the selected streaming model name here; the engine
    /// holder and the primary-ready gates read it.
    /// </summary>
    public Winpepper.Core.Settings.StreamingModelSelectionSlot StreamingModelSelection { get; }

    public Winpepper.Core.Settings.CleanupModelSelectionSlot CleanupModelSelection { get; }
    public Winpepper.Cleanup.CleanupBackendHolder CleanupBackend { get; }

    /// <summary>
    /// Background first-run installer for the Nemotron streaming model +
    /// native runtime. Kicked off (non-blocking) from <see cref="StartAsync"/>;
    /// the Models page reads its status so the streaming card reflects an
    /// in-flight or failed auto-install.
    /// </summary>
    public Winpepper.Models.StreamingAutoInstaller StreamingAutoInstaller { get; }

    /// <summary>
    /// Background multi-model onboarding downloads (speech model first, with
    /// deep verification + engine load probe). Lives on the shell so the
    /// downloads survive the onboarding page being navigated away from, and
    /// so boot reconciliation can resume interrupted optional downloads.
    /// </summary>
    public Winpepper.Core.ViewModels.IOnboardingModelProvisioner OnboardingProvisioner { get; }

    private readonly WinUiSoundEffectPlayer _sounds;

    public static AppShell Create()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var logTail = new Winpepper.Core.Logging.LogRingBuffer(capacity: 2000);
        var factory = Winpepper.Core.Logging.WinpepperLogging.CreateWithBuffer(
            AppPaths.LogsDir, debugConsole: false,
            minimumLevel: LogLevel.Information,
            buffer: logTail);
        var store = new SettingsStore(AppPaths.SettingsJson,
            onError: msg => factory.CreateLogger("Winpepper.App.Settings").LogWarning("{SettingsWarning}", msg));
        var settings = store.Load();
        var modelsServices = new Winpepper.App.Services.ModelsServices(
            Path.Combine(AppPaths.Root, "models"), settings.AsrModelName);
        if (!string.Equals(settings.AsrModelName, modelsServices.AsrDescriptor.Name, StringComparison.Ordinal))
        {
            factory.CreateLogger("Winpepper.App").LogWarning(
                "Unknown ASR model {ConfiguredModel}; restored default {DefaultModel}",
                settings.AsrModelName, modelsServices.AsrDescriptor.Name);
            settings = settings with { AsrModelName = modelsServices.AsrDescriptor.Name };
            // Boot-time validity repair — the ONE sanctioned direct save:
            // it runs before the DebouncedSettingsWriter exists (below),
            // and the writer re-loads from disk at every flush, so this
            // can neither clobber nor be clobbered. ALL runtime settings
            // writes go through SettingsWriter (single write authority).
            store.Save(settings);
        }
        var streamingResolved = modelsServices.Registry.ResolveOrDefault(
            settings.StreamingModelName, Winpepper.Models.ModelKind.StreamingAsr).Name;
        if (!string.Equals(settings.StreamingModelName, streamingResolved, StringComparison.Ordinal))
        {
            factory.CreateLogger("Winpepper.App").LogWarning(
                "Unknown streaming model {ConfiguredModel}; restored default {DefaultModel}",
                settings.StreamingModelName, streamingResolved);
            settings = settings with { StreamingModelName = streamingResolved };
            store.Save(settings); // same boot-time window as the AsrModelName repair above
        }
        // Slot BEFORE holder: the holder's selection delegate reads it.
        var streamingSelection = new Winpepper.Core.Settings.StreamingModelSelectionSlot();
        streamingSelection.Publish(settings.StreamingModelName); // seed with the persisted boot value
        var nemotronHolder = new NemotronEngineHolder(
            modelsServices.ModelsRoot, factory.CreateLogger<NemotronEngineHolder>(),
            () => streamingSelection.Read() ?? settings.StreamingModelName);
        // Same first-run treatment the batch model gets via onboarding, minus
        // the blocking: constructed here, kicked off in StartAsync. It shares
        // ModelsServices (and therefore the Models page's operation gate), so
        // an Install click during the auto-install can never double-download.
        var streamingAutoInstaller = new Winpepper.Models.StreamingAutoInstaller(
            modelsServices.Registry, modelsServices.ModelsRoot, modelsServices);
        var onboardingProvisioner = new Winpepper.App.Services.OnboardingModelProvisioner(
            modelsServices,
            factory.CreateLogger<Winpepper.App.Services.OnboardingModelProvisioner>(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            engineLoadProbe: (name, ct) => Task.Run(() =>
            {
                // One-shot engine load probe: spawn a worker for the selected
                // layout, force the Load RPC (a tiny batch drives spawn+Load),
                // dispose. Failure -> the provisioner publishes the sticky
                // redist/repair error instead of a false "ready".
                try
                {
                    var layout = Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(name);
                    var exe = Environment.ProcessPath
                        ?? throw new InvalidOperationException("no process path for the probe worker");
                    using var probe = new Winpepper.Asr.TranscribeCpp.Worker.WorkerProcessEngine(
                        new Winpepper.Asr.TranscribeCpp.Worker.ExeWorkerProcessFactory(
                            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker")),
                        layout.RuntimeDir(modelsServices.ModelsRoot),
                        layout.GgufPath(modelsServices.ModelsRoot),
                        layout.Name);
                    probe.TranscribeBatch(new float[1600], layout.Language, out _); // 0.1 s of silence
                    return true;
                }
                catch { return false; }
            }, ct));
        var asrSelection = new Winpepper.Core.Settings.AsrModelSelectionSlot();
        asrSelection.Publish(settings.AsrModelName); // seed with the persisted boot value
        var writer = new DebouncedSettingsWriter(store,
            log: factory.CreateLogger("Winpepper.App.Settings"));

        var uiThread = new DispatcherQueueUiThread(DispatcherQueue.GetForCurrentThread());
        var engine = new SessionEngine();
        var sessionVm = new SessionViewModel(engine, uiThread,
            log: factory.CreateLogger<SessionViewModel>());
        // Pegged-indicator sampling reuses the ONE existing sampler mechanism
        // (GetSystemTimes via ProcessResourceSampler); returns null off-Windows.
        sessionVm.SystemTimesSampler = () =>
            Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes() is { } s
                ? (s.Idle100ns, s.Kernel100ns, s.User100ns)
                : null;
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
            // Consumer toast policy: only interrupt the user when they can act
            // on it. Non-actionable reports stay on the bus (Diagnostics page)
            // and in the logs, but never pop UI. See ErrorToastPolicy.
            if (!Winpepper.Core.Errors.ErrorToastPolicy.ShouldToast(rec.Stage)) return;
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
                        // ShowMain() must run BEFORE NavigateToTag: when the
                        // main window was closed, ShowMain() constructs a new
                        // MainWindow, so navigating first targets the dead
                        // window and the freshly shown one opens on its
                        // default tab instead of the toast's deep link.
                        uiThread.Post(() =>
                        {
                            var shell = Winpepper.App.App.Shell;
                            if (shell is null) return;
                            shell.ShowMain();
                            (shell.Main as Winpepper.App.Views.MainWindow)?.NavigateToTag(t.Result);
                        });
                    }
                });
        });
        var hotkeyValidator = new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator();
        var recordingVm = new RecordingSettingsViewModel(settings, writer, hotkeyValidator);
        // Cleanup settings persist into AppSettings (Cleanup* properties) and are
        // read LIVE per dictation by PipelineHost, so a Cleanup-tab change (incl.
        // the Enabled toggle) takes effect on the very next dictation.
        // Hoisted above the settings VMs so honesty delegates (cleanup VM, pipeline
        // prefetch gate) can close over the same active-model source the cleanup
        // call uses.
        var cleanupSelection = new Winpepper.Core.Settings.CleanupModelSelectionSlot();
        cleanupSelection.Publish(settings.CleanupModelName); // seed with the persisted boot value

        Func<string?, Winpepper.Cleanup.CleanupModelTarget> resolveCleanupTarget = raw =>
        {
            // CleanupModelResolution -> CleanupModelTarget: field-for-field
            // copy (Winpepper.Cleanup does not reference Winpepper.Models).
            var r = Winpepper.Models.CleanupModelPathResolver.Resolve(
                modelsServices.Registry, modelsServices.ModelsRoot, raw);
            return new Winpepper.Cleanup.CleanupModelTarget(
                r.GgufPath, r.ResolvedName, r.FellBackToDefault,
                r.PromptFormat, r.OmitPromptExample);
        };

        var cleanupContract = CleanupSettingsContract.FromSettings(settings);
        var cleanupVm = new CleanupSettingsViewModel(cleanupContract,
            c => _ = writer.QueueAndFlushAsync(c.ApplyTo),
            promptSettingsSupported: () =>
                Winpepper.Cleanup.PromptFormatCapabilities.CarriesSystemPrompt(
                    resolveCleanupTarget(cleanupSelection.Read()).PromptFormat));

        // Corrections: the store must exist before the VM so the VM can seed
        // from disk and persist back through it (the dictation pipeline reads
        // the same file). Store construction stays optional: if it fails, the
        // UI still works in-memory for this session and cleanup runs with
        // empty corrections.
        Winpepper.Corrections.CorrectionStore? correctionStore = null;
        try
        {
            correctionStore = new Winpepper.Corrections.CorrectionStore(AppPaths.CorrectionsJson);
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "CorrectionStore unavailable; cleanup will run with empty corrections.");
        }

        var correctionsVm = correctionStore is not null
            ? Winpepper.Corrections.CorrectionsWiring.CreateViewModel(
                correctionStore,
                onError: ex =>
                {
                    factory.CreateLogger("Winpepper.App").LogWarning(ex,
                        "Corrections persistence failed; edits are kept in memory for this session.");
                    errorBus.Report(Winpepper.Core.Errors.ErrorStage.Learning, ex, Guid.Empty);
                })
            : new CorrectionsViewModel(
                Array.Empty<string>(),
                new Dictionary<string, string>(),
                (_, _) => { /* no store: in-memory only for this session */ });

        var autostart = new AutostartRegistry();
        var sounds = new WinUiSoundEffectPlayer(AppPaths.AssetsDir) { Enabled = settings.PlaySounds };

        // PLAN2-TYPE — Plan 2 owns these types; constructing them here so Plan 3's
        // pipeline can invoke real cleanup + window context. Each one is optional —
        // if the model or registry isn't present yet, we fall back to raw transcript.
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null;

        // Live cleanup-model swap (mirror of the ASR slot + seam): the holder
        // owns backend+runner construction, hash-verified readiness, pre-warm,
        // and disposal; PipelineHost consumes it once per dictation at the
        // cleanup seam. Boot no longer blocks on the GGUF load —
        // RequestPrewarm loads in the background and a dictation that wins the
        // race falls back to the raw transcript once, then self-heals.
        var cleanupHolder = new Winpepper.Cleanup.CleanupBackendHolder(
            desiredModelName: () => cleanupSelection.Read(),
            resolve: resolveCleanupTarget,
            verifyReady: name => modelsServices.VerifyCleanupModelReady(name),
            backendFactory: target => new Winpepper.Cleanup.LlamaCleanupBackend(
                target.GgufPath!,
                factory.CreateLogger<Winpepper.Cleanup.LlamaCleanupBackend>(),
                promptFormat: target.PromptFormat),
            runnerFactory: (backend, omit) => new Winpepper.Cleanup.CleanupRunner(
                backend,
                factory.CreateLogger<Winpepper.Cleanup.CleanupRunner>(),
                omitPromptExample: omit),
            log: factory.CreateLogger<Winpepper.Cleanup.CleanupBackendHolder>(),
            // Pre-warm = load + first-generation warm-up (ledger A5): WarmAsync
            // pages in weights + Vulkan shader pipeline and swallows its own
            // failures as non-fatal. Cast is safe by construction: the
            // backendFactory above always constructs LlamaCleanupBackend.
            warmup: (backend, ct) =>
                ((Winpepper.Cleanup.LlamaCleanupBackend)backend).WarmAsync(ct));
        cleanupHolder.RequestPrewarm(); // replaces the old synchronous boot load

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

        // NOTE: no boot-time CleanupOptions snapshot here. PipelineHost builds
        // CleanupOptions per dictation from the settings provider
        // (Winpepper.Cleanup.CleanupOptionsFactory.FromSettings), so Cleanup-tab
        // changes are live.

        var historyServices = new Winpepper.App.Services.HistoryServices(
            AppPaths.HistoryRoot,
            new Winpepper.History.Lab.LocalTranscriptionRerunService(
                name =>
                {
                    var engine = nemotronHolder.TryGet(); // serves the CURRENTLY SELECTED streaming model
                    return Winpepper.History.Lab.RerunModelRouter.EngineServes(engine?.ModelName, name) ? engine : null;
                },
                name => modelsServices.Registry.Find(name)?.Kind == Winpepper.Models.ModelKind.StreamingAsr),
            () => store.Load());

        var cancel = HotkeyChord.Parse("Esc");
        var hotkeyLog = factory.CreateLogger("Winpepper.App.Hotkeys");
        // A hand-edited settings file must never bind a bare common key (it would
        // be swallowed system-wide). Unsafe/invalid values fall back to the
        // built-in defaults with a logged warning.
        var hold   = HotkeyChord.ParseTriggerOrDefault(
            settings.HoldHotkey, "RightCtrl+RightShift", cancel,
            m => hotkeyLog.LogWarning("{HotkeyWarning}", m),
            allowLongPressSpace: true);
        var toggle = HotkeyChord.ParseTriggerOrDefault(
            settings.ToggleHotkey, "Ctrl+Shift+Space", cancel,
            m => hotkeyLog.LogWarning("{HotkeyWarning}", m));
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

        // --- AssemblyAI cloud ASR provider stack (optional; key may be absent) ---
        var aaiKeyStore = new Winpepper.Asr.Transcription.AssemblyAiKeyStore(
            AppPaths.AssemblyAiKeyFile, new Winpepper.App.Asr.DpapiApiKeyProtector());
        // No global HttpClient.Timeout: per-request timeouts are enforced inside
        // AssemblyAiClient via a linked CTS, and the total cloud budget is owned by
        // FallbackTranscriber. A single large safety cap guards against a truly wedged socket.
        var aaiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var aaiOptions = new Winpepper.Asr.Transcription.AssemblyAiOptions
        {
            Model = settings.AssemblyAiModel,
            CloudDeadline = Winpepper.Asr.Transcription.AssemblyAiOptions.ClampDeadline(settings.AssemblyAiCloudDeadlineSeconds),
            DeleteAfterTranscribe = settings.AssemblyAiDeleteAfterTranscribe,
            KeytermsEnabled = settings.AssemblyAiKeytermsEnabled,
        };
        var aaiClient = new Winpepper.Asr.Transcription.AssemblyAiClient(
            aaiHttp,
            () => aaiKeyStore.Load(),
            aaiOptions,
            factory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());

        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel,
                                         name => modelsServices.Registry.InstallDirFor(
                                             modelsServices.ModelsRoot, name, Winpepper.Models.ModelKind.Asr),
                                         () => asrSelection.Read(),
                                         raw => modelsServices.Registry.ResolveOrDefault(
                                             raw, Winpepper.Models.ModelKind.Asr).Name,
                                         name => modelsServices.VerifyAsrModelReady(name),
                                         (dir, name) => new Winpepper.Asr.Transcription.ParakeetTranscriber(
                                             new Winpepper.Asr.ParakeetSession(dir), name, ownsSession: true),
                                         () => modelsServices.IsStreamingModelInstalled(streamingSelection.Read()
                                             ?? settings.StreamingModelName),
                                         historyServices.Archiver, cleanupHolder,
                                         clipboardFallback, toasts,
                                         () => store.Load(),
                                         (backup, backupName, s, onFallback) =>
                                         {
                                             var layout = nemotronHolder.CurrentLayout;
                                             return AppShell.BuildStreamingTranscriber(
                                                 backup, backupName, s, onFallback, () => nemotronHolder.TryGet(),
                                                 layout.Name, layout.Language,
                                                 aaiClient, aaiKeyStore, aaiOptions,
                                                 correctionStore, errorBus, factory);
                                         },
                                         correctionStore, windowContext,
                                         postPaste: postPaste, focusedCapturer: focusedCapturer,
                                         postPasteLearningEnabled: settings.PostPasteLearningEnabled,
                                         prewarmMicEnabled: settings.PrewarmMicEnabled,
                                         activeCleanupPromptFormat: () => resolveCleanupTarget(cleanupSelection.Read()).PromptFormat);

        // Shell publication and StartAsync are now driven by PublishedStartup
        // (App.OnLaunched): Create() stays synchronous and returns the fully
        // constructed shell. Keep our AssemblyAI ctor params — they must be
        // assigned before MainWindow is built inside the ctor.
        return new AppShell(factory, store, settings, writer, engine, sessionVm, errorBus,
                            recordingVm, cleanupVm, correctionsVm,
                            autostart, pipeline, sounds, historyServices, modelsServices,
                            toasts, clipboardFallback, crashHandler,
                            logTail, uiThread, diagHost,
                            aaiKeyStore, aaiClient, aaiOptions, asrSelection,
                            streamingSelection,
                            cleanupSelection, cleanupHolder,
                            streamingAutoInstaller, onboardingProvisioner);
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
                     Winpepper.App.Hosting.DiagnosticsHost diagnosticsHost,
                     Winpepper.Asr.Transcription.IAssemblyAiKeyStore assemblyAiKeyStore,
                     Winpepper.Asr.Transcription.AssemblyAiClient assemblyAiClient,
                     Winpepper.Asr.Transcription.AssemblyAiOptions assemblyAiOptions,
                     Winpepper.Core.Settings.AsrModelSelectionSlot asrSelection,
                     Winpepper.Core.Settings.StreamingModelSelectionSlot streamingSelection,
                     Winpepper.Core.Settings.CleanupModelSelectionSlot cleanupSelection,
                     Winpepper.Cleanup.CleanupBackendHolder cleanupHolder,
                     Winpepper.Models.StreamingAutoInstaller streamingAutoInstaller,
                     Winpepper.Core.ViewModels.IOnboardingModelProvisioner onboardingProvisioner)
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
        // Assigned BEFORE MainWindow below: its NavigationView selection
        // synchronously navigates to RecordingPage, which reads these.
        AssemblyAiKeyStore = assemblyAiKeyStore;
        AssemblyAiClient = assemblyAiClient;
        AssemblyAiOptions = assemblyAiOptions;
        AsrModelSelection = asrSelection;
        StreamingModelSelection = streamingSelection;
        CleanupModelSelection = cleanupSelection;
        CleanupBackend = cleanupHolder;
        StreamingAutoInstaller = streamingAutoInstaller;
        OnboardingProvisioner = onboardingProvisioner;

        Pill = new StatusPillWindow(sessionVm);
        // Clicking the pill in its PENDING state pastes the held text into the
        // field focused at click time, via the normal injection path.
        Pill.PastePendingHandler = Pipeline.TryPastePending;
        Tray = new TrayIconHost(sessionVm, AppPaths.AssetsDir, "0.3.0",
                                 openSettings: ShowMain, quit: Quit,
                                 log: factory.CreateLogger<TrayIconHost>());
        Main = new MainWindow(this);
    }

    internal async Task StartAsync()
    {
        var startHidden = Environment.GetEnvironmentVariable("WINPEPPER_START_HIDDEN") == "1";
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
        else if (!startHidden) ShowMain(navigateToOnboarding: false);
        // else: stay tray-only (autostart with --tray).

        // One-time content-island realization, off the bootstrap call stack (see RealizeOnce doc).
        Pill.RealizeOnce();

        // On-device visual verification: force-show the pill with a synthetic
        // level sweep (no audio needed) so a screenshot/pixel probe can verify
        // the capsule silhouette and voice meter. Dev/diagnostics only.
        if (Environment.GetEnvironmentVariable("WINPEPPER_PILL_PREVIEW") == "1")
        {
            var previewLog = LogFactory.CreateLogger<StatusPillWindow>();
            Main?.DispatcherQueue.TryEnqueue(() => Pill.StartPreview(previewLog));
        }

        // Start only after first paint and authoritative size/hash verification.
        // A merely loadable stale model must not enter PipelineHost and later
        // satisfy onboarding through PipelineHost.IsRunning.
        try
        {
            var startupGate = new AsrPipelineStartupGate(
                ct => ModelsServices.VerifyPrimarySpeechReadyAsync(
                    StreamingModelSelection.Read() ?? Settings.StreamingModelName, ct),
                Pipeline.TryStart,
                onNotReady: () => ErrorBus.Report(
                    Winpepper.Core.Errors.ErrorStage.Asr,
                    new FileNotFoundException(
                        "Speech model is missing or failed verification. Open Models to download or repair it."),
                    Guid.Empty));
            await startupGate.TryStartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogFactory.CreateLogger<AppShell>()
                .LogError(ex, "ASR verification failed during pipeline startup");
            ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
        }

        // Background auto-install of the SELECTED streaming model + native
        // runtime, non-blocking: dictation works immediately on whatever is
        // installed, and streaming activates on the first dictation after the
        // install lands (PipelineHost re-checks installed state per
        // dictation). StartAsync never throws; a failure leaves the installed
        // models fully functional and is re-attempted on the next launch or
        // from the Models card (shared operation gate — no double download).
        // No error-bus report: streaming is an enhancement, and the Models
        // card surfaces the failed state with a retry. On a fresh install the
        // attempt is deferred until onboarding completes (see below).
        _ = Task.Run(async () =>
        {
            var log = LogFactory.CreateLogger("Winpepper.App.StreamingAutoInstall");
            try
            {
                // Read a fresh settings snapshot on the background thread (the
                // injected Settings is a startup-time snapshot) and use it for
                // both checks below.
                var settings = SettingsStore.Load();

                // Fresh install: onboarding now OWNS the model installs (the
                // picker downloads the selected streaming model itself), so
                // this background repair path defers until onboarding
                // completes; on later launches it re-installs a missing
                // selected model as before.
                if (!settings.OnboardingCompleted)
                {
                    log.LogInformation(
                        "Onboarding not completed; deferring the streaming model auto-install to the next launch");
                    return;
                }

                await StreamingAutoInstaller.StartAsync(
                    settings.StreamingEnabled, settings.StreamingModelName, CancellationToken.None);
                switch (StreamingAutoInstaller.Status)
                {
                    case Winpepper.Models.StreamingAutoInstallStatus.Installed:
                        log.LogInformation("Nemotron streaming model is installed");
                        break;
                    case Winpepper.Models.StreamingAutoInstallStatus.SkippedStreamingDisabled:
                        log.LogInformation("Streaming disabled; skipped the streaming model auto-install");
                        break;
                    case Winpepper.Models.StreamingAutoInstallStatus.Failed:
                        log.LogWarning(
                            "Nemotron streaming model auto-install failed (batch dictation unaffected; " +
                            "retried next launch or via the Models tab): {AutoInstallError}",
                            StreamingAutoInstaller.LastError);
                        break;
                }

                // V6/A17: picker-chosen optional downloads interrupted by app exit
                // would otherwise never complete (the onboarding page is the only
                // other initiator and it is unreachable once onboarding completes).
                if (settings.OnboardingCompleted)
                {
                    bool Missing(string name) => ModelsServices.Registry.Find(name) is { } d
                        && !d.IsFullyInstalledAndExtracted(ModelsServices.ModelsRoot);
                    if ((settings.OnboardingBackupModelChosen && Missing(settings.AsrModelName))
                        || (settings.OnboardingCleanupModelChosen && Missing(settings.CleanupModelName)))
                    {
                        var scope = new List<string> { settings.StreamingModelName };
                        if (settings.OnboardingBackupModelChosen) scope.Add(settings.AsrModelName);
                        if (settings.OnboardingCleanupModelChosen) scope.Add(settings.CleanupModelName);
                        OnboardingProvisioner.StartDownloads(scope, settings.StreamingModelName);
                    }
                }
            }
            catch (Exception ex)
            {
                // Defensive: StartAsync's contract is never-throw.
                log.LogWarning(ex, "Streaming model auto-install crashed unexpectedly");
            }
        });
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

    /// <summary>
    /// Builds the streaming transcriber for a dictation. Local: nemotron-first
    /// via LocalStreamingTranscriberFactory (streaming when enabled+installed;
    /// Nemotron batch otherwise; optional Parakeet backup as the second ladder
    /// rung). Cloud (AssemblyAI): wrapped in FallbackStreamingTranscriber over
    /// the same local batch ladder. Static, dependencies explicit, invoked
    /// through PipelineHost's injected delegate.
    /// </summary>
    public static Winpepper.Asr.Transcription.IStreamingTranscriber BuildStreamingTranscriber(
        Winpepper.Asr.Transcription.ITranscriber? parakeetBackup,   // null when not installed
        string? backupModelName,                                    // loaded backup name or null
        AppSettings settings,
        Action<string> onFallback,
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?>? nemotronEngine,
        string streamingModelName,
        string? streamingLanguage,
        Winpepper.Asr.Transcription.IAssemblyAiClient client,
        Winpepper.Asr.Transcription.IAssemblyAiKeyStore keyStore,
        Winpepper.Asr.Transcription.AssemblyAiOptions options,
        Winpepper.Corrections.CorrectionStore? correctionStore,
        Winpepper.Core.Errors.ErrorBus errorBus,
        ILoggerFactory loggerFactory)
    {
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?> engine = () => nemotronEngine?.Invoke();

        if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
        {
            return Winpepper.Asr.Transcription.LocalStreamingTranscriberFactory.Build(
                engine, parakeetBackup, streamingModelName, streamingLanguage,
                settings.StreamingEnabled, loggerFactory);
        }

        var localBatch = Winpepper.Asr.Transcription.LocalStreamingTranscriberFactory.BuildBatchLadder(
            engine, parakeetBackup, streamingModelName, streamingLanguage, loggerFactory);

        // Snapshot corrections into request extras at build time; keyterms only
        // when opted in — copied verbatim from today's BuildTranscriber. The REST
        // batch transcriber is the streaming session's zero-pushed fallback (A9).
        Winpepper.Asr.Transcription.AssemblyAiRequestExtras Extras()
        {
            var data = correctionStore?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;
            return Winpepper.Asr.Transcription.CorrectionSpellingMapper.ToExtras(data, options.KeytermsEnabled);
        }

        var cloudBatch = new Winpepper.Asr.Transcription.AssemblyAiTranscriber(
            client, keyStore, options,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiTranscriber>(),
            extrasProvider: Extras);

        var cloud = new Winpepper.Asr.Transcription.AssemblyAiStreamingTranscriber(
            () => new Winpepper.Asr.Transcription.ClientStreamingWebSocket(),
            cloudBatch, keyStore, options,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiStreamingTranscriber>());

        return new Winpepper.Asr.Transcription.FallbackStreamingTranscriber(
            cloud, localBatch,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.FallbackStreamingTranscriber>(),
            onFallback: onFallback,
            cloudDeadline: options.CloudDeadline,
            onConfigError: msg => errorBus.Report(
                // Models, NOT Asr: this fires per dictation attempt and the
                // dictation then SUCCEEDS via local fallback, so it is a
                // per-attempt EVENT. At Asr it would classify as a CONDITION
                // whose only clearing seam (local model Load/Swap success)
                // never runs for a cloud user - a permanent tray error while
                // every dictation works. Behavior is otherwise identical:
                // ErrorDeepLink maps Asr and Models both to "models"/"Open
                // Models tab" and ErrorToastPolicy toasts both.
                Winpepper.Core.Errors.ErrorStage.Models,
                new InvalidOperationException(
                    $"AssemblyAI model rejected ({settings.AssemblyAiModel}). Check the model setting. {msg}"),
                Guid.Empty)); // config-level error, not tied to a capture session
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        // After Pipeline, and ONLY if its run loop actually joined: then no
        // dictation holds a cleanup lease and disposing the live backend
        // cannot race a generation (serialized-caller invariant). On a
        // timed-out join (loop orphaned, possibly mid-generation — ledger A2)
        // deliberately LEAK the holder: Application.Current.Exit() follows
        // immediately, and a leak is safe where a use-after-free is not.
        if (Pipeline.RunLoopJoined) CleanupBackend.Dispose();
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
