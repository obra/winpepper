#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Core.Audio;
using Winpepper.Core.Pending;
using Winpepper.Core.Sessions;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;

namespace Winpepper.App.Hosting;

/// <summary>
/// Plan-3 pipeline host. Wires audio capture → ASR → cleanup (Plan 2) → injection.
/// Cleanup, corrections, and window-context are optional — if absent the raw
/// transcript is injected unchanged (Plan-1 behaviour).
/// Plan-4: each phase is timed and the result is archived via HistoryArchiver.
/// </summary>
public sealed class PipelineHost : IDisposable
{
    private readonly ILogger<PipelineHost> _log;
    private readonly HotkeyHook _hook;
    private readonly HotkeyReadinessGate _hotkeyReadiness = new();
    private readonly HotkeyLifecycleGate _hotkeyLifecycle = new(nameof(PipelineHost));
    private bool _hotkeyLoopStarted;
    private readonly TextInjector _injector;
    private ParakeetSession? _asr;
    private readonly Func<string, string> _resolveModelDir;
    private readonly Func<string?> _desiredAsrModel;
    private readonly Func<string?, string> _resolveAsrModelName;
    private readonly Func<string, bool> _isAsrModelReady;
    private readonly Winpepper.Core.Asr.AsrModelSwapState _asrSwap = new();
    private readonly SessionEngine _engine;
    private readonly SessionViewModel _vm;
    private readonly ISoundEffectPlayer _sounds;
    private IWarmAudioRecorder? _warmRecorder;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly object _startGate = new();
    private Action<Exception>? _captureFaultHandler;
    private Action? _captureRecoveredHandler;
    private Action<ReadOnlyMemory<float>>? _frameHandler;

    private readonly Winpepper.Cleanup.CleanupRunner? _cleanup;        // PLAN2-TYPE
    private readonly Winpepper.Cleanup.CleanupOptions _cleanupOptions; // PLAN2-TYPE
    private readonly Winpepper.Corrections.CorrectionStore? _corrections; // PLAN2-TYPE
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetch? _windowContext; // PLAN2-TYPE
    private Task<Winpepper.Platform.WindowContext.WindowContextResult>? _ctxPrefetchTask;    // PLAN2-TYPE

    private readonly Winpepper.History.HistoryArchiver _archiver;
    private readonly string _cleanupModelName;
    private System.Diagnostics.Stopwatch? _recordStopwatch;

    private readonly Winpepper.Core.Errors.ErrorBus _errorBus;
    private Guid _currentSessionId = Guid.Empty;
    private readonly Winpepper.Platform.Injection.ClipboardFallback _clipboardFallback;
    private readonly Winpepper.Core.Notifications.IToastService _toasts;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> _buildTranscriber;
    private readonly Winpepper.Core.Learning.PostPasteWatcher? _postPaste;
    private readonly Winpepper.Platform.Learning.FocusedElementCapturer? _focusedCapturer;
    private InjectionTarget _targetAtStart = InjectionTarget.Empty;
    private readonly bool _postPasteLearningEnabled;
    private readonly bool _prewarmMicEnabled;

    public PipelineHost(
        ILoggerFactory factory,
        Winpepper.Core.Errors.ErrorBus errorBus,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        Func<string, string> resolveModelDir,
        Func<string?> desiredAsrModelName,
        Func<string?, string> resolveAsrModelName,
        Func<string, bool> isAsrModelReady,
        Winpepper.History.HistoryArchiver archiver,
        string cleanupModelName,
        Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
        Winpepper.Core.Notifications.IToastService toasts,
        Func<AppSettings> settingsProvider,
        Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> transcriberFactory,
        Winpepper.Cleanup.CleanupRunner? cleanup = null,                       // PLAN2-TYPE
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Cleanup.CleanupOptions? cleanupOptions = null,               // PLAN2-TYPE
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null,
        bool postPasteLearningEnabled = false,
        bool prewarmMicEnabled = true)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _errorBus = errorBus;
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(
            hold,
            toggle,
            cancel,
            factory.CreateLogger<HotkeyHook>(),
            cancelEnabled: () => _engine.State != SessionState.Idle,
            normalTriggersEnabled: () => _hotkeyReadiness.IsEnabled);
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _resolveModelDir = resolveModelDir;
        _desiredAsrModel = desiredAsrModelName;
        _resolveAsrModelName = resolveAsrModelName;
        _isAsrModelReady = isAsrModelReady;
        _archiver = archiver;
        _cleanupModelName = cleanupModelName;
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
        _cleanupOptions = cleanupOptions ?? new Winpepper.Cleanup.CleanupOptions();
        _clipboardFallback = clipboardFallback;
        _toasts = toasts;
        _settingsProvider = settingsProvider;
        _buildTranscriber = transcriberFactory;
        _postPaste = postPaste;
        _focusedCapturer = focusedCapturer;
        _postPasteLearningEnabled = postPasteLearningEnabled;
        _prewarmMicEnabled = prewarmMicEnabled;
    }

    /// <summary>True once the ASR model is loaded and the hotkey pipeline is running.</summary>
    public bool IsRunning { get; private set; }

    public void UpdateHotkeys(string hold, string toggle)
        => _hotkeyLifecycle.Run(() =>
        {
            _hook.UpdateChords(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle));
            return true;
        });

    public IDisposable BeginHotkeyCapture(Action<RawKeyTransition> sink) =>
        _hotkeyLifecycle.Run(() =>
        {
            EnsureHotkeyLoopStarted();
            return _hook.BeginRawCapture(sink);
        });

    private void EnsureHotkeyLoopStarted()
    {
        if (_hotkeyLoopStarted) return;
        _hook.Start();
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
        _hotkeyLoopStarted = true;
        _log.LogInformation("Hotkey hook/event loop started");
    }

    /// <summary>
    /// Loads the ASR model and starts the hotkey pipeline. Safe to call again
    /// later — e.g. after the Models tab finishes downloading. When the model
    /// files are missing (fresh install, issue #6) the pipeline stays disabled,
    /// the condition is reported on the error bus (which deep-links to the
    /// Models tab), and the method returns false instead of throwing.
    /// </summary>
    public bool TryStart() => _hotkeyLifecycle.Run(() =>
    {
        // Raw recorder capture is required during onboarding, before a model is
        // installed. Keep the event loop draining normal triggers while gated.
        EnsureHotkeyLoopStarted();
        return TryStartCore();
    });

    /// <summary>
    /// Ensure the local ASR session matches the currently-selected model.
    /// Called only from the serialized run loop (`await foreach` + inline
    /// `await HandleHotkey`), so it can never race another dictation; it takes
    /// <see cref="_startGate"/> around all session mutation, including disposal
    /// of the old session. Resolves the canonical descriptor name FIRST, feeds
    /// the decider descriptor-level VERIFIED readiness (size + SHA-256, cached
    /// per selection change), loads the first session, swaps to a newly
    /// selected model (disposing the old one), or keeps the current session
    /// when the selection is unchanged or the desired model is not yet
    /// verified-ready. On load failure the previous working session is kept
    /// and, when <paramref name="reportErrors"/> is true, the error is
    /// reported; the cloud path passes false to soften the local error surface.
    /// Returns true iff a usable session is loaded afterward.
    /// </summary>
    private bool TryEnsureAsrModel(bool reportErrors = true)
    {
        lock (_startGate)
        {
            // Read the desired name from the in-memory slot — NOT from
            // _settingsProvider(): the settings-file round-trip is not a safe
            // cross-thread transport (a Windows atomic replace can fail against
            // this loop's open read handle, silently dropping a promote).
            // Then resolve FIRST: unknown/null/"" values fall back to the
            // default descriptor via ModelRegistry.ResolveOrDefault, so the
            // decider only ever sees canonical catalog names. Planning or
            // committing the raw name would record a model that never ran and
            // cause spurious swaps between two unknown names.
            var desired = _resolveAsrModelName(_desiredAsrModel());
            var desiredDir = _resolveModelDir(desired);
            // Descriptor-level verified readiness (per-file size + SHA-256 via
            // ModelProvisioningCoordinator.VerifyReadyAsync, cached per
            // selection change by ModelsServices) — NOT a bare File.Exists.
            // "A merely loadable stale model must not enter PipelineHost."
            var ready = _isAsrModelReady(desired);
            var action = _asrSwap.Plan(desired, ready);

            switch (action)
            {
                case Winpepper.Core.Asr.AsrSwapAction.KeepCurrent:
                    return _asr is not null;

                case Winpepper.Core.Asr.AsrSwapAction.CannotStart:
                    _log.LogWarning(
                        "ASR model {Model} not verified-ready in {ModelDir}; pipeline disabled until models are downloaded",
                        desired, desiredDir);
                    if (reportErrors)
                    {
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                            Guid.Empty);
                    }
                    return false;

                case Winpepper.Core.Asr.AsrSwapAction.Load:
                case Winpepper.Core.Asr.AsrSwapAction.Swap:
                    try
                    {
                        var previousModel = _asrSwap.LoadedModelName;
                        var fresh = new ParakeetSession(desiredDir);
                        var old = _asr;
                        _asr = fresh;
                        _asrSwap.CommitLoad(desired);
                        old?.Dispose(); // under _startGate; idempotent (Step 5)
                        _log.LogInformation(
                            "ASR model loaded (swap #{Generation}): {Previous} -> {Model}",
                            _asrSwap.Generation, previousModel ?? "(none)", desired);
                        // Recovery success for the Asr CONDITION ("no usable
                        // speech model"): a model that loads is proof the
                        // condition is over.
                        _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Asr);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Failed to load ASR model {Model} from {ModelDir}; keeping previous session",
                            desired, desiredDir);
                        if (reportErrors && _asr is null)   // no usable session at all -> the ongoing condition
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
                        else if (reportErrors)              // kept the old working model -> per-attempt failure
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
                        return _asr is not null; // keep-old-on-failure
                    }

                default:
                    return _asr is not null;
            }
        }
    }

    private bool TryStartCore()
    {
        lock (_startGate)
        {
            if (IsRunning) return true;
            if (!TryEnsureAsrModel())
                return false;
            // Bug-2: one warm recorder for the app lifetime. Frames flow (and the
            // meter animates) only while a session is active, so subscribe once.
            if (_warmRecorder is null)
            {
                var recorder = new Winpepper.Audio.WarmWasapiRecorder(
                    prewarm: _prewarmMicEnabled,
                    deviceId: null,
                    log: _log);
                _frameHandler = frame => _vm.ReportAudioFrame(frame);
                _captureFaultHandler = ex =>
                {
                    // Capture faults are logged and recorded for Diagnostics but
                    // show NO toast: recovery is automatic (endpoint-driven
                    // rebuild + session-start rebuild), so there is nothing for
                    // the user to act on. The actionable failure - a dictation
                    // that captured no audio - has its own toast at session end
                    // (WarnIfSessionSilent). Consumer toast policy: see
                    // ErrorToastPolicy (Audio stage is silent on the bus too).
                    //
                    // Wrapped in MicrophoneUnavailableException so the taxonomy
                    // can tell this ONGOING condition apart from the
                    // per-dictation "no audio detected" EVENT, which arrives at
                    // the same stage. The inner message is preserved verbatim.
                    _log.LogError(ex, "microphone capture faulted");
                    _errorBus.Report(
                        Winpepper.Core.Errors.ErrorStage.Audio,
                        new Winpepper.Core.Errors.MicrophoneUnavailableException(ex),
                        _currentSessionId);
                };
                recorder.FramesAvailable += _frameHandler;
                recorder.CaptureFaulted += _captureFaultHandler;
                _captureRecoveredHandler = () =>
                    // The recorder raises CaptureRecovered only after a
                    // non-empty frame has been observed from the live source -
                    // the one signal that cannot lie (IsRunning after a rebuild
                    // can; a validity probe can). This is the ONLY thing that
                    // clears the microphone condition. It can fire TWICE for
                    // one episode (frame path + the fault handler's reconcile),
                    // which is safe: NotifyConditionRecovered is idempotent.
                    _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Audio);
                recorder.CaptureRecovered += _captureRecoveredHandler;
                _warmRecorder = recorder;
            }
            // NOTE: the hook + event loop are started by EnsureHotkeyLoopStarted()
            // in TryStart() (upstream design: the loop may run pre-model for
            // onboarding capture, gated by _hotkeyReadiness). Starting the hook
            // again here throws "HotkeyHook already started" (merge regression).
            // Upstream hotkey readiness gate: mark hotkeys replay-safe now that
            // the event loop is running. RunAsync consults ShouldHandle(evt).
            _hotkeyReadiness.Enable(DateTimeOffset.UtcNow);
            IsRunning = true;
            _log.LogInformation("Pipeline started (ASR model {Model})", _asrSwap.LoadedModelName);
            return true;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _hook.Events.ReadAllAsync(ct))
            {
                if (!_hotkeyReadiness.ShouldHandle(evt))
                {
                    _log.LogDebug("Ignoring {HotkeyKind} while dictation pipeline is not ready", evt.Kind);
                    continue;
                }
                try { await HandleHotkey(evt, ct); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "pipeline error");
                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Unknown, ex, _currentSessionId);
                    _engine.Apply(SessionEvent.Failed);
                    _vm.NotifyError(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Capture the current focused-field identity as a pure InjectionTarget.
    /// Maps the Windows-only FocusedElementSnapshot into the platform-agnostic
    /// identity the pure decider compares. Returns Empty when capture is
    /// unavailable (no capturer) or fails (invalid snapshot) — the decider then
    /// defaults to InjectNow, preserving today's behavior.
    /// </summary>
    private InjectionTarget CaptureTarget()
    {
        if (_focusedCapturer is null) return InjectionTarget.Empty;
        var snap = _focusedCapturer.Capture();
        if (!snap.IsValid) return InjectionTarget.Empty;
        return new InjectionTarget
        {
            WindowHandle = snap.ForegroundHwnd.ToInt64(),
            ElementId = snap.ElementId,
        };
    }

    /// <summary>
    /// Paste the held pending text into whatever field is focused NOW (the
    /// user's explicit choice via the pill click). Uses the normal injection
    /// path. On success the VM consumes the slot and hides the pill; on failure
    /// the pending slot is kept (NotifyPasteAttempted(false)) so the pill stays
    /// in its click-to-paste state and the user simply clicks again — no toast,
    /// no clipboard clobbering (consumer policy: the pill IS the surface).
    /// Returns true when the paste succeeded. Runs on the UI thread.
    /// </summary>
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = Winpepper.Core.InjectionText.ForPaste(_vm.PendingPasteText);
        var injected = !string.IsNullOrWhiteSpace(text) && _injector.TryInject(text);
        if (!injected)
        {
            // Slot is kept below; the pill stays clickable for a retry.
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; pending slot kept for retry"),
                _currentSessionId);
        }
        if (injected)
            _log.LogInformation("Pending paste injected");
        else
            _log.LogWarning("Pending paste injection failed");

        return _vm.NotifyPasteAttempted(injected);
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                if (_vm.HasPendingPaste)
                    _log.LogInformation("Pending paste discarded unpasted");
                _engine.Apply(SessionEvent.StartRequested);
                _currentSessionId = Guid.NewGuid();
                _log.LogInformation("Session started (hold) {SessionId}", _currentSessionId);
                _sounds.PlayStart();
                _warmRecorder!.StartSession(includePrerollMs: 500);
                _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _targetAtStart = CaptureTarget();

                // PLAN2-TYPE — start window-context prefetch in parallel with audio capture.
                _ctxPrefetchTask = null;
                if (_windowContext is not null && _cleanupOptions.WindowContextEnabled)
                {
                    var hwnd = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
                    _ctxPrefetchTask = _windowContext.StartAsync(hwnd, ct);
                }
                break;
            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                _recordStopwatch?.Stop();

                var samples = _warmRecorder!.StopSession();
                WarnIfSessionSilent(samples, _currentSessionId);
                _sounds.PlayStop();

                var trimmed = TrimForTranscription(samples, _currentSessionId);
                if (trimmed is null)
                {
                    // Live-mic silence: skip transcription + injection (the ASR-saving
                    // point of the feature) but STILL archive the ORIGINAL buffer,
                    // exactly like an empty-final-text dictation does today. This keeps
                    // the drop non-destructive: a mis-classified real dictation stays
                    // recoverable in history. Then complete like an empty-final-text
                    // dictation (Transcribing -> Injecting -> Idle) so the pill returns
                    // to idle. No cleanup, no injection, no toast.
                    _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                    {
                        Samples16k = samples,
                        RawTranscript = "",
                        CleanedText = "",
                        AsrModelName = "",
                        CleanupModelName = "",
                        WindowContextUsed = false,
                        WindowTitleAtStart = "",
                        WindowTitleAtInject = "",
                        Timings = new Winpepper.History.HistoryTimings
                        {
                            RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                            TranscribeMs = 0,
                            CleanupMs = 0,
                            InjectMs = 0,
                            TotalMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                        },
                    });
                    _engine.Apply(SessionEvent.TranscriptReady);
                    _engine.Apply(SessionEvent.InjectionCompleted);
                    _ctxPrefetchTask = null;
                    _recordStopwatch = null;
                    break;
                }

                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                var settingsNow = _settingsProvider();
                var cloudSelected = string.Equals(
                    settingsNow.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                // Provider-aware (req 6): a failed LOCAL swap never skips or
                // aborts a CLOUD dictation; soften its error surface.
                var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                if ((!localReady && !cloudSelected) || _asr is null)
                {
                    // Terminal-state early-exit (S2): never bare-return — drive
                    // the engine back so the next dictation can start.
                    _engine.Apply(SessionEvent.Failed);
                    if (cloudSelected && _asr is null)
                    {
                        // Cloud selected but no local session exists at all (the
                        // fallback wrapper needs one): surface this rare case.
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                            new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                            Guid.Empty);
                    }
                    _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                    return;
                }
                var transcriber = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow, notice =>
                    _ = _toasts.ShowAsync(
                        "Winpepper",
                        "Cloud transcription unavailable — used local speech recognition instead.",
                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                        TimeSpan.FromSeconds(6)));
                var transcription = await transcriber.TranscribeAsync(trimmed, ct);
                transcribeSw.Stop();
                _engine.Apply(SessionEvent.TranscriptReady);

                string final = transcription.Text;
                var producedModelName = transcription.ProviderModelName;
                var cleanupSw = new System.Diagnostics.Stopwatch();
                var cleanupUsedModel = "";
                var windowContextUsed = false;

                if (!string.IsNullOrWhiteSpace(final) && _cleanup is not null)
                {
                    _vm.MarkCleaningUp();

                    // Plan 2's CleanupRunner.RunAsync expects a Task<string?>? for the
                    // window context. Adapt our Task<WindowContextResult> by projecting
                    // .Text out (or null on failure). This mirrors Plan 2 Cli/Pipeline.cs
                    // lines 3749-3751.
                    Task<string?>? ctxTextTask = null;
                    if (_ctxPrefetchTask is not null)
                    {
                        ctxTextTask = _ctxPrefetchTask.ContinueWith(
                            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                            ct,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    var correctionsData = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                    cleanupSw.Start();
                    try
                    {
                        var result = await _cleanup.RunAsync(
                            rawTranscript: final,
                            corrections: correctionsData,
                            windowContextTask: ctxTextTask,
                            options: _cleanupOptions,
                            ct: ct,
                            skipLlm: Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName));
                        cleanupSw.Stop();
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                        cleanupUsedModel = result.Path == Winpepper.Cleanup.CleanupPath.BypassProvider
                            ? "none (cloud, corrections-only)"
                            : _cleanupModelName;
                        windowContextUsed = ctxTextTask is not null
                                            && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                    }
                    catch (Exception ex)
                    {
                        cleanupSw.Stop();
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                    }
                }

                var injectSw = System.Diagnostics.Stopwatch.StartNew();
                var injected = false;
                if (!string.IsNullOrWhiteSpace(final))
                {
                    var targetAtInject = CaptureTarget();
                    var decision = PendingPasteDecider.Decide(_targetAtStart, targetAtInject);
                    if (decision == InjectionDecision.HoldPending)
                    {
                        // Focus moved to a different known field: do NOT inject anywhere.
                        // Hold the text as an in-memory pending paste (memory-only slot).
                        // Owner decision (2026-07-22): the dictation is STILL archived at
                        // completion below, exactly like an injected one — the pending
                        // slot itself remains memory-only and is not what gets persisted.
                        _vm.EnterPendingPaste(final, _targetAtStart);
                        _log.LogInformation(
                            "Held as pending paste ({Chars} chars, {Words} words)",
                            final.Length,
                            final.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                    }
                    else
                    {
                        var toType = Winpepper.Core.InjectionText.ForPaste(final);
                        injected = _injector.TryInject(toType);
                        if (!injected)
                        {
                            // Injection failed (SendInput refused). Consumer policy:
                            // no toast, no clipboard clobbering — hold the text as a
                            // pending paste so the pill shows "Click to paste" and
                            // the user pastes on their own terms.
                            _errorBus.Report(
                                Winpepper.Core.Errors.ErrorStage.Injection,
                                new InvalidOperationException("SendInput refused; held as pending paste"),
                                _currentSessionId);
                            _vm.EnterPendingPaste(final, _targetAtStart);
                            _log.LogInformation(
                                "Injection failed; held as pending paste ({Chars} chars)",
                                final.Length);
                        }
                    }
                }
                injectSw.Stop();
                if (Winpepper.Core.Learning.PostPasteGate.ShouldWatch(
                        _postPasteLearningEnabled, injected,
                        _postPaste is not null, _focusedCapturer is not null,
                        !string.IsNullOrWhiteSpace(final)))
                {
                    var snap = _focusedCapturer!.Capture();
                    if (snap.IsValid)
                    {
                        var watchTask = _postPaste!.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
                        {
                            ElementId = snap.ElementId,
                            InjectedText = final,
                            SessionId = _currentSessionId,
                            InjectionEndUtc = DateTime.UtcNow,
                        });
                        var sid = _currentSessionId;
                        _ = watchTask.ContinueWith(t =>
                        {
                            if (t.Exception is not null)
                                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Learning,
                                                  t.Exception.GetBaseException(), sid);
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                }
                _engine.Apply(SessionEvent.InjectionCompleted);

                var totalMs = (int)((_recordStopwatch?.ElapsedMilliseconds ?? 0)
                                     + transcribeSw.ElapsedMilliseconds
                                     + cleanupSw.ElapsedMilliseconds
                                     + injectSw.ElapsedMilliseconds);
                _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                {
                    Samples16k = samples,
                    RawTranscript = transcription.Text,
                    CleanedText = final,
                    AsrModelName = producedModelName,
                    CleanupModelName = cleanupUsedModel,
                    WindowContextUsed = windowContextUsed,
                    WindowTitleAtStart = "",
                    WindowTitleAtInject = "",
                    Timings = new Winpepper.History.HistoryTimings
                    {
                        RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                        TranscribeMs = (int)transcribeSw.ElapsedMilliseconds,
                        CleanupMs = (int)cleanupSw.ElapsedMilliseconds,
                        InjectMs = (int)injectSw.ElapsedMilliseconds,
                        TotalMs = totalMs,
                    },
                });

                _ctxPrefetchTask = null;
                _recordStopwatch = null;
                break;
            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _ = _warmRecorder?.StopSession();
                break;
            case HotkeyEventKind.Toggle:
                if (_engine.State == SessionState.Idle)
                {
                    if (_vm.HasPendingPaste)
                        _log.LogInformation("Pending paste discarded unpasted");
                    _engine.Apply(SessionEvent.StartRequested);
                    _currentSessionId = Guid.NewGuid();
                    _log.LogInformation("Session started (toggle) {SessionId}", _currentSessionId);
                    _sounds.PlayStart();
                    _warmRecorder!.StartSession(includePrerollMs: 500);
                    _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _targetAtStart = CaptureTarget();

                    // PLAN2-TYPE — start window-context prefetch in parallel with audio capture.
                    _ctxPrefetchTask = null;
                    if (_windowContext is not null && _cleanupOptions.WindowContextEnabled)
                    {
                        var hwnd = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
                        _ctxPrefetchTask = _windowContext.StartAsync(hwnd, ct);
                    }
                }
                else if (_engine.State == SessionState.Recording)
                {
                    _engine.Apply(SessionEvent.StopRequested);
                    _recordStopwatch?.Stop();

                    var samples2 = _warmRecorder!.StopSession();
                    WarnIfSessionSilent(samples2, _currentSessionId);
                    _sounds.PlayStop();

                    var trimmed2 = TrimForTranscription(samples2, _currentSessionId);
                    if (trimmed2 is null)
                    {
                        // Live-mic silence: skip transcription + injection but STILL
                        // archive the ORIGINAL buffer (empty transcript), exactly like
                        // an empty-final-text dictation does today, so the drop is
                        // non-destructive. Then complete like an empty-final-text
                        // dictation (Transcribing -> Injecting -> Idle). No cleanup,
                        // no injection, no toast. See the HoldUp block for details.
                        _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                        {
                            Samples16k = samples2,
                            RawTranscript = "",
                            CleanedText = "",
                            AsrModelName = "",
                            CleanupModelName = "",
                            WindowContextUsed = false,
                            WindowTitleAtStart = "",
                            WindowTitleAtInject = "",
                            Timings = new Winpepper.History.HistoryTimings
                            {
                                RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                                TranscribeMs = 0,
                                CleanupMs = 0,
                                InjectMs = 0,
                                TotalMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                            },
                        });
                        _engine.Apply(SessionEvent.TranscriptReady);
                        _engine.Apply(SessionEvent.InjectionCompleted);
                        _ctxPrefetchTask = null;
                        _recordStopwatch = null;
                        break;
                    }

                    var transcribeSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var settingsNow2 = _settingsProvider();
                    var cloudSelected2 = string.Equals(
                        settingsNow2.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                    var localReady2 = TryEnsureAsrModel(reportErrors: !cloudSelected2);
                    if ((!localReady2 && !cloudSelected2) || _asr is null)
                    {
                        _engine.Apply(SessionEvent.Failed);
                        if (cloudSelected2 && _asr is null)
                        {
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                                new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                                Guid.Empty);
                        }
                        _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                        return;
                    }
                    var transcriber2 = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow2, notice =>
                        _ = _toasts.ShowAsync(
                            "Winpepper",
                            "Cloud transcription unavailable — used local speech recognition instead.",
                            Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                            TimeSpan.FromSeconds(6)));
                    var transcription2 = await transcriber2.TranscribeAsync(trimmed2, ct);
                    transcribeSw2.Stop();
                    _engine.Apply(SessionEvent.TranscriptReady);

                    string final2 = transcription2.Text;
                    var producedModelName2 = transcription2.ProviderModelName;
                    var cleanupSw2 = new System.Diagnostics.Stopwatch();
                    var cleanupUsedModel2 = "";
                    var windowContextUsed2 = false;

                    if (!string.IsNullOrWhiteSpace(final2) && _cleanup is not null)
                    {
                        _vm.MarkCleaningUp();

                        // Plan 2's CleanupRunner.RunAsync expects a Task<string?>? for the
                        // window context. Adapt our Task<WindowContextResult> by projecting
                        // .Text out (or null on failure). This mirrors Plan 2 Cli/Pipeline.cs
                        // lines 3749-3751.
                        Task<string?>? ctxTextTask2 = null;
                        if (_ctxPrefetchTask is not null)
                        {
                            ctxTextTask2 = _ctxPrefetchTask.ContinueWith(
                                t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                                ct,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }

                        var correctionsData2 = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                        cleanupSw2.Start();
                        try
                        {
                            var result2 = await _cleanup.RunAsync(
                                rawTranscript: final2,
                                corrections: correctionsData2,
                                windowContextTask: ctxTextTask2,
                                options: _cleanupOptions,
                                ct: ct,
                                skipLlm: Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName2));
                            cleanupSw2.Stop();
                            _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                                result2.Path, (int)result2.Elapsed.TotalMilliseconds);
                            final2 = result2.CleanedText;
                            cleanupUsedModel2 = result2.Path == Winpepper.Cleanup.CleanupPath.BypassProvider
                                ? "none (cloud, corrections-only)"
                                : _cleanupModelName;
                            windowContextUsed2 = ctxTextTask2 is not null
                                                && result2.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                        }
                        catch (Exception ex)
                        {
                            cleanupSw2.Stop();
                            _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                        }
                    }

                    var injectSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var injected2 = false;
                    if (!string.IsNullOrWhiteSpace(final2))
                    {
                        var targetAtInject2 = CaptureTarget();
                        var decision2 = PendingPasteDecider.Decide(_targetAtStart, targetAtInject2);
                        if (decision2 == InjectionDecision.HoldPending)
                        {
                            // Focus moved to a different known field: do NOT inject anywhere.
                            // Hold the text as an in-memory pending paste (memory-only slot).
                            // Owner decision (2026-07-22): the dictation is STILL archived at
                            // completion below, exactly like an injected one — the pending
                            // slot itself remains memory-only and is not what gets persisted.
                            _vm.EnterPendingPaste(final2, _targetAtStart);
                            _log.LogInformation(
                                "Held as pending paste ({Chars} chars, {Words} words)",
                                final2.Length,
                                final2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                        }
                        else
                        {
                            var toType2 = Winpepper.Core.InjectionText.ForPaste(final2);
                            injected2 = _injector.TryInject(toType2);
                            if (!injected2)
                            {
                                // See hold path: failure -> pending click-to-paste,
                                // no toast, no clipboard clobbering.
                                _errorBus.Report(
                                    Winpepper.Core.Errors.ErrorStage.Injection,
                                    new InvalidOperationException("SendInput refused; held as pending paste"),
                                    _currentSessionId);
                                _vm.EnterPendingPaste(final2, _targetAtStart);
                                _log.LogInformation(
                                    "Injection failed; held as pending paste ({Chars} chars)",
                                    final2.Length);
                            }
                        }
                    }
                    injectSw2.Stop();
                    if (Winpepper.Core.Learning.PostPasteGate.ShouldWatch(
                            _postPasteLearningEnabled, injected2,
                            _postPaste is not null, _focusedCapturer is not null,
                            !string.IsNullOrWhiteSpace(final2)))
                    {
                        var snap = _focusedCapturer!.Capture();
                        if (snap.IsValid)
                        {
                            var watchTask = _postPaste!.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
                            {
                                ElementId = snap.ElementId,
                                InjectedText = final2,
                                SessionId = _currentSessionId,
                                InjectionEndUtc = DateTime.UtcNow,
                            });
                            var sid = _currentSessionId;
                            _ = watchTask.ContinueWith(t =>
                            {
                                if (t.Exception is not null)
                                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Learning,
                                                      t.Exception.GetBaseException(), sid);
                            }, TaskContinuationOptions.OnlyOnFaulted);
                        }
                    }
                    _engine.Apply(SessionEvent.InjectionCompleted);

                    var totalMs2 = (int)((_recordStopwatch?.ElapsedMilliseconds ?? 0)
                                         + transcribeSw2.ElapsedMilliseconds
                                         + cleanupSw2.ElapsedMilliseconds
                                         + injectSw2.ElapsedMilliseconds);
                    _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                    {
                        Samples16k = samples2,
                        RawTranscript = transcription2.Text,
                        CleanedText = final2,
                        AsrModelName = producedModelName2,
                        CleanupModelName = cleanupUsedModel2,
                        WindowContextUsed = windowContextUsed2,
                        WindowTitleAtStart = "",
                        WindowTitleAtInject = "",
                        Timings = new Winpepper.History.HistoryTimings
                        {
                            RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                            TranscribeMs = (int)transcribeSw2.ElapsedMilliseconds,
                            CleanupMs = (int)cleanupSw2.ElapsedMilliseconds,
                            InjectMs = (int)injectSw2.ElapsedMilliseconds,
                            TotalMs = totalMs2,
                        },
                    });

                    _ctxPrefetchTask = null;
                    _recordStopwatch = null;
                }
                break;
        }
    }

    /// <summary>
    /// Silence-trims the finished recording for TRANSCRIPTION ONLY. Returns the
    /// trimmed samples to send to ASR, or <c>null</c> when the recording has no
    /// speech (live mic, nobody spoke) and the caller should DROP the dictation.
    /// Logs a content-free info line for either outcome. The ORIGINAL buffer is
    /// still archived by the caller — only the transcription input is trimmed.
    /// Runs AFTER WarnIfSessionSilent, so a dead-mic session has already toasted
    /// (actionable); the quiet drop below adds no toast (consumer policy: a
    /// live-mic-nobody-spoke drop is not actionable).
    /// </summary>
    private float[]? TrimForTranscription(float[] samples, Guid sessionId)
    {
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples);
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            _log.LogInformation("dropped silent recording, {Ms} ms", ms);
            return null;
        }

        if (result.RemovedMs > 0)
            _log.LogInformation(
                "trimmed silence: {Ms} ms across {Runs} runs",
                result.RemovedMs, result.RunsTrimmed);

        return result.Trimmed;
    }

    /// <summary>
    /// Bug 2: if a whole session captured essentially zero energy (OS mic mute,
    /// privacy toggle, Bluetooth hiccup), the transcript will be empty for a
    /// reason the user cannot see. Surface it via the ErrorBus + a toast. Never
    /// called mid-session — only after StopSession — so genuine mid-session
    /// silence is not misreported.
    /// </summary>
    private void WarnIfSessionSilent(float[] samples, Guid sessionId)
    {
        if (samples.Length == 0) return; // nothing captured is a distinct (cancel) case
        if (!Winpepper.Audio.AudioEnergy.IsSessionSilent(samples)) return;

        _log.LogWarning("Session {SessionId} captured near-zero energy (RMS below the zero-energy threshold — mic likely muted / privacy-off / disconnected)", sessionId);
        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Audio,
            new InvalidOperationException("No audio detected — check your microphone / privacy settings."),
            sessionId);
        _ = _toasts.ShowAsync(
            "Winpepper",
            "No audio detected — check your microphone / privacy settings.",
            Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
            TimeSpan.FromSeconds(6));
    }

    public void Dispose()
    {
        // Upstream serializes teardown against hotkey startup/capture via the
        // lifecycle gate and disables the readiness gate first. Inside it we run
        // OUR warm-recorder teardown (unhook meter + fault handlers before
        // dispose) instead of the old single _recorder field.
        _hotkeyLifecycle.Dispose(() =>
        {
            _hotkeyReadiness.Disable();
            _runCts?.Cancel();
            try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _hook.Dispose();
            lock (_startGate)
            {
                _asr?.Dispose();
                _asr = null;
            }
            if (_warmRecorder is not null)
            {
                // Bug 8 (hygiene): unhook the meter + fault handlers before teardown.
                if (_frameHandler is not null) _warmRecorder.FramesAvailable -= _frameHandler;
                if (_captureFaultHandler is not null) _warmRecorder.CaptureFaulted -= _captureFaultHandler;
                if (_captureRecoveredHandler is not null) _warmRecorder.CaptureRecovered -= _captureRecoveredHandler;
                _warmRecorder.Dispose();
            }
        });
    }
}
#endif
