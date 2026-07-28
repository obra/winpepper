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
    // Item B: a drain-timeout (or teardown) abandon can orphan a streaming pump
    // still executing a native call on the shared ParakeetSession; every dispose
    // of that session routes through this guard so it can never race a live pump.
    private readonly Winpepper.Core.Asr.OrphanedPumpGuard _orphanGuard;
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
    private Winpepper.Asr.Transcription.StreamingDictationSession? _streamingSession;
    private Action<ReadOnlyMemory<float>>? _streamFrameHandler;

    private readonly Winpepper.Cleanup.CleanupBackendHolder _cleanupHolder;
    // NOTE: no CleanupOptions field. Options are built PER DICTATION from the
    // settings provider (CleanupOptionsFactory.FromSettings) so Cleanup-tab
    // changes — including the Enabled toggle — take effect immediately.
    private readonly Winpepper.Corrections.CorrectionStore? _corrections; // PLAN2-TYPE
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetch? _windowContext; // PLAN2-TYPE
    private Task<Winpepper.Platform.WindowContext.WindowContextResult>? _ctxPrefetchTask;    // PLAN2-TYPE

    private readonly Winpepper.History.HistoryArchiver _archiver;
    private System.Diagnostics.Stopwatch? _recordStopwatch;

    private readonly Winpepper.Core.Errors.ErrorBus _errorBus;
    private Guid _currentSessionId = Guid.Empty;
    private readonly Winpepper.Platform.Injection.ClipboardFallback _clipboardFallback;
    private readonly Winpepper.Core.Notifications.IToastService _toasts;
    private readonly Func<AppSettings> _settingsProvider;
    private readonly Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> _buildTranscriber;
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
        Winpepper.Cleanup.CleanupBackendHolder cleanupHolder,
        Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
        Winpepper.Core.Notifications.IToastService toasts,
        Func<AppSettings> settingsProvider,
        Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> transcriberFactory,
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null,
        bool postPasteLearningEnabled = false,
        bool prewarmMicEnabled = true)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _orphanGuard = new(ex => _log.LogWarning(ex, "deferred ASR session dispose failed"));
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
        _cleanupHolder = cleanupHolder;
        _corrections = corrections;
        _windowContext = windowContext;
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

    /// <summary>True once Dispose has joined the run loop (or it was never
    /// started / already terminal). When FALSE the loop was orphaned, possibly
    /// mid-dictation: the cleanup holder must NOT be disposed (leak instead —
    /// the process is exiting).</summary>
    public bool RunLoopJoined { get; private set; } = true;

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
                        // Under _startGate; idempotent (Step 5). Routed through the
                        // orphan guard: if an abandoned streaming pump may still be
                        // executing a native call on the old session, the dispose is
                        // deferred until that pump completes (RunOrDefer never blocks).
                        if (old is not null) _orphanGuard.RunOrDefer(old.Dispose);
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
                // Streaming tee: a permanent handler that forwards frames to the
                // current dictation's streaming session (null outside dictations,
                // so this is a no-op at idle). OnFrame copies and never blocks.
                _streamFrameHandler = frame => _streamingSession?.OnFrame(frame);
                recorder.FramesAvailable += _streamFrameHandler;
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
    /// OR a mid-paste focus change the pending slot is kept (full text) so the
    /// user simply clicks again — no toast,
    /// no clipboard clobbering (consumer policy: the pill IS the surface).
    /// Returns true when the paste succeeded. Runs on the UI thread.
    /// </summary>
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = Winpepper.Core.InjectionText.ForPaste(_vm.PendingPasteText);
        var outcome = string.IsNullOrWhiteSpace(text)
            ? Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed
            : _injector.TryInjectGuarded(text);
        var injected = outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
        if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed)
        {
            // Slot is kept below; the pill stays clickable for a retry.
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; pending slot kept for retry"),
                _currentSessionId);
        }
        if (injected)
            _log.LogInformation("Pending paste injected");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
            // The clicked-into window is elevated: UIPI would have silently
            // dropped every keystroke while reporting success (the exact
            // failure this check exists for -- previously the slot was
            // consumed and the text lost). Nothing was typed; the slot keeps
            // the FULL text and the pill copy tells the user to focus a
            // normal window first. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste blocked: foreground window is elevated; slot kept with full text");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
            // Focus moved mid-paste during the pill-click retry too: the slot
            // still holds the FULL original text, so the next click re-pastes
            // all of it. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste interrupted (focus, modifier, or mouse-button change); slot kept with full text for another click");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.NoForeground)
            // No observable foreground at click time (hwnd==0 transient):
            // nothing was typed; the slot keeps the FULL text for another
            // click. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste deferred: no observable foreground at click time; slot kept with full text");
        else
            _log.LogWarning("Pending paste injection failed");

        // Keep the pill copy in sync with the LATEST attempt: an elevated
        // block shows the admin-window copy; any other kept-slot outcome
        // restores the default "Click to paste" (the previous attempt may
        // have set the admin copy).
        if (!injected)
            _vm.ShowPendingPasteStatus(
                outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated
                    ? Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget
                    : Winpepper.Core.Pending.PendingPasteReason.Interrupted);

        return _vm.NotifyPasteAttempted(injected);
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                if (_vm.HasPendingPaste)
                    _log.LogInformation(
                        "Pending paste retained across new dictation ({Chars} chars held; a park during this dictation will append)",
                        _vm.PendingPasteText.Length);
                _engine.Apply(SessionEvent.StartRequested);
                _currentSessionId = Guid.NewGuid();
                _log.LogInformation("Session started (hold) {SessionId}", _currentSessionId);
                _sounds.PlayStart();
                // Start the streaming dictation session BEFORE StartSession —
                // StartSession raises the 500 ms pre-roll synchronously through
                // FramesAvailable, so the session must already exist (frames
                // queue in the coordinator until the factory completes) or the
                // cloud stream permanently loses the first ~500 ms. The factory
                // runs on a background pump so recording start stays instant;
                // model ensure is silent here (reportErrors: false) — the stop
                // arm's late path re-runs the check with today's exact error UX.
                var settingsForStream = _settingsProvider();
                if (settingsForStream.StreamingEnabled)
                {
                    _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                        ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                        {
                            var cloudSel = string.Equals(settingsForStream.AsrProvider, "assemblyai",
                                StringComparison.OrdinalIgnoreCase);
                            var ready = TryEnsureAsrModel(reportErrors: false);
                            if ((!ready && !cloudSel) || _asr is null) return null;
                            return _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsForStream, notice =>
                                _ = _toasts.ShowAsync(
                                    "Winpepper",
                                    "Cloud transcription unavailable — used local speech recognition instead.",
                                    Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                                    TimeSpan.FromSeconds(6)));
                        }, ct2),
                        _log, ct);
                }
                else
                {
                    _log.LogDebug("streaming disabled by settings; batch transcription will run at stop");
                }
                _warmRecorder!.StartSession(includePrerollMs: 500);
                _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _targetAtStart = CaptureTarget();

                // PLAN2-TYPE — start window-context prefetch in parallel with audio
                // capture. Gated on LIVE settings (not a boot snapshot) so a
                // Cleanup-tab change applies to this dictation; prefetch is only
                // useful when the cleanup LLM is enabled at all.
                _ctxPrefetchTask = null;
                var settingsAtStart = _settingsProvider();
                if (_windowContext is not null
                    && settingsAtStart.CleanupEnabled
                    && settingsAtStart.CleanupWindowContextEnabled)
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
                    if (_streamingSession is not null)
                    {
                        var droppedStreaming = _streamingSession;
                        _streamingSession = null;
                        await droppedStreaming.DisposeAsync();
                        NoteStreamingReleased(droppedStreaming);
                    }
                    _recordStopwatch = null;
                    break;
                }

                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                var settingsNow = _settingsProvider();
                var cloudSelected = string.Equals(
                    settingsNow.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                Action<string> fallbackNotice = notice =>
                    _ = _toasts.ShowAsync(
                        "Winpepper",
                        "Cloud transcription unavailable — used local speech recognition instead.",
                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                        TimeSpan.FromSeconds(6));
                // Finish the streaming session FIRST — before ANY TryEnsureAsrModel
                // call: the ensure's swap branch disposes the ParakeetSession the
                // streaming transcriber still holds (no engine-state gating), so
                // no ensure may run while a session is in flight. The factory's
                // own ensure (start arm) ran before the session captured the
                // model. A mid-dictation model change applies to the NEXT dictation.
                var streaming = _streamingSession;
                _streamingSession = null;
                Winpepper.Asr.Transcription.TranscriptionResult? maybeTranscription = null;
                if (streaming is not null)
                {
                    // Dispose the coordinator on ALL paths — including a FinishAsync
                    // throw (pump-error rethrow, inner-session throw, ct cancel):
                    // otherwise the inner streaming session (e.g. cloud websocket)
                    // leaks. DisposeAsync is idempotent, never throws, and is a
                    // near-no-op after a successful FinishAsync or a drain-timeout
                    // (both already disposed the inner session internally).
                    try
                    {
                        maybeTranscription = await streaming.FinishAsync(trimmed, ct);
                    }
                    finally
                    {
                        await streaming.DisposeAsync();
                        // If the abandon left the pump orphaned (drain timeout /
                        // bounded dispose wait expired), register it so no model
                        // dispose can race the native call it may still be in.
                        NoteStreamingReleased(streaming);
                    }
                }
                if (maybeTranscription is null)
                {
                    // Late path: no streaming session materialized, its factory
                    // returned null (no provider at start), or the drain deadline
                    // expired on a wedged connection (session already abandoned +
                    // disposed inside FinishAsync). Run today's ensure + error UX,
                    // then the batch-equivalent path via the streaming seam. The
                    // cloud wrapper bounds BOTH its connect (StartSessionAsync)
                    // and its post-stop wait with the cloud deadline (Task 8), so
                    // even on the wedged network that caused a drain timeout this
                    // path cannot hang the serial hotkey loop.
                    // Provider-aware (req 6): a failed LOCAL swap never skips or
                    // aborts a CLOUD dictation; soften its error surface.
                    // Drain-timeout abandon (A5 residual, Item B): running the
                    // ensure here is safe even after a drain timeout — the
                    // abandoned pump was registered with _orphanGuard above
                    // (inside the FinishAsync finally), so the ensure's Swap
                    // branch DEFERS the old session's dispose until that pump
                    // completes instead of racing its in-flight native call.
                    var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                    var asrNow = _asr;
                    if ((!localReady && !cloudSelected) || asrNow is null)
                    {
                        if (streaming is not null)
                        {
                            await streaming.DisposeAsync(); // no-op after FinishAsync; never throws
                        }
                        // Terminal-state early-exit (S2): never bare-return — drive
                        // the engine back so the next dictation can start.
                        _engine.Apply(SessionEvent.Failed);
                        if (cloudSelected && asrNow is null)
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
                    var transcriber = _buildTranscriber(asrNow, _asrSwap.LoadedModelName!, settingsNow, fallbackNotice);
                    await using var lateSession = await transcriber.StartSessionAsync(ct);
                    maybeTranscription = await lateSession.FinishAsync(trimmed, ct);
                }
                var transcription = maybeTranscription;
                transcribeSw.Stop();

                string final = transcription.Text;
                var producedModelName = transcription.ProviderModelName;
                var cleanupSw = new System.Diagnostics.Stopwatch();
                var cleanupUsedModel = "";
                var windowContextUsed = false;

                // Live per-dictation cleanup settings: the Cleanup tab persists into
                // AppSettings, so building options HERE (not at boot) makes the
                // Enabled toggle and every other cleanup setting take effect on
                // this very dictation.
                // Per-dictation cleanup-model seam (mirror of TryEnsureAsrModel):
                // adopt a completed pre-warm and swap HERE — never mid-generation.
                // The serialized run loop (await foreach + inline await
                // HandleHotkey) guarantees the previous dictation's RunAsync has
                // completed, so disposing the replaced backend is safe.
                var cleanupLease = _cleanupHolder.EnsureCurrent();
                var cleanupRunner = cleanupLease.Runner;
                var cleanupOptions = Winpepper.Cleanup.CleanupOptionsFactory.FromSettings(settingsNow);
                var skipLlm = Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName);
                // Single policy home (CleanupRunner.Preflight) decides whether the
                // LLM will actually run. The engine enters CleaningUp exactly for
                // those dictations — so the pill's "Cleaning up..." phase is
                // truthful and "Inserting..." remains reachable afterwards.
                var llmWillRun = !string.IsNullOrWhiteSpace(final) && cleanupRunner is not null
                    && Winpepper.Cleanup.CleanupRunner.Preflight(final, cleanupOptions, skipLlm);
                _engine.Apply(llmWillRun ? SessionEvent.CleanupStarted : SessionEvent.TranscriptReady);

                if (!string.IsNullOrWhiteSpace(final) && cleanupRunner is not null)
                {
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
                        var result = await cleanupRunner.RunAsync(
                            rawTranscript: final,
                            corrections: correctionsData,
                            windowContextTask: ctxTextTask,
                            options: cleanupOptions,
                            ct: ct,
                            skipLlm: skipLlm);
                        cleanupSw.Stop();
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                        cleanupUsedModel = result.Path switch
                        {
                            Winpepper.Cleanup.CleanupPath.BypassProvider => "none (cloud, corrections-only)",
                            Winpepper.Cleanup.CleanupPath.BypassDisabled => "none (disabled, corrections-only)",
                            _ => cleanupLease.LoadedModelName ?? "",
                        };
                        windowContextUsed = ctxTextTask is not null
                                            && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                    }
                    catch (Exception ex)
                    {
                        cleanupSw.Stop();
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                    }

                    // Exit CleaningUp whether the runner succeeded or threw — the
                    // engine must reach Injecting either way.
                    if (llmWillRun) _engine.Apply(SessionEvent.CleanupCompleted);
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
                        var outcome = _injector.TryInjectGuarded(toType);
                        injected = outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
                        if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
                        {
                            // Focus moved to another window (or a halt-gesture
                            // modifier or mouse button went down) while the keystrokes
                            // were still going out: stop typing and hold the WHOLE
                            // transcription as a pending paste (never just the
                            // remainder -- a torn partial paste in the old window
                            // means the user re-pastes ALL of it where they want it).
                            // Not an error: no ErrorBus report, no toast, no
                            // clipboard clobbering -- the pill is the surface.
                            _vm.EnterPendingPaste(final, _targetAtStart);
                            _log.LogInformation(
                                "Injection interrupted (focus, modifier, or mouse-button change); held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
                        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
                        {
                            // The target window is elevated: UIPI silently
                            // drops SendInput while reporting success, so
                            // nothing was typed. Park the WHOLE transcription
                            // and explain via the pill copy. Not an error:
                            // no ErrorBus report, no toast.
                            _vm.EnterPendingPaste(final, _targetAtStart,
                                Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget);
                            _log.LogInformation(
                                "Injection blocked: foreground window is elevated; held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
                        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.NoForeground)
                        {
                            // No observable foreground at send start
                            // (hwnd==0; probe-gated park polarity, council
                            // 2026-07-28): nothing was typed. Park the WHOLE
                            // transcription with the default copy. Not an
                            // error: no ErrorBus report, no toast -- the
                            // pill is the surface.
                            _vm.EnterPendingPaste(final, _targetAtStart);
                            _log.LogInformation(
                                "No observable foreground at injection start; held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
                        else if (!injected)
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
                if (_streamingSession is not null)
                {
                    var cancelledStreaming = _streamingSession;
                    _streamingSession = null;
                    await cancelledStreaming.DisposeAsync();
                    NoteStreamingReleased(cancelledStreaming);
                }
                break;
            case HotkeyEventKind.Toggle:
                if (_engine.State == SessionState.Idle)
                {
                    if (_vm.HasPendingPaste)
                        _log.LogInformation(
                            "Pending paste retained across new dictation ({Chars} chars held; a park during this dictation will append)",
                            _vm.PendingPasteText.Length);
                    _engine.Apply(SessionEvent.StartRequested);
                    _currentSessionId = Guid.NewGuid();
                    _log.LogInformation("Session started (toggle) {SessionId}", _currentSessionId);
                    _sounds.PlayStart();
                    // (same comment as the HoldDown arm: create BEFORE StartSession
                    // so the synchronously-raised pre-roll is not dropped)
                    var settingsForStream2 = _settingsProvider();
                    if (settingsForStream2.StreamingEnabled)
                    {
                        _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                            ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                            {
                                var cloudSel2 = string.Equals(settingsForStream2.AsrProvider, "assemblyai",
                                    StringComparison.OrdinalIgnoreCase);
                                var ready2 = TryEnsureAsrModel(reportErrors: false);
                                if ((!ready2 && !cloudSel2) || _asr is null) return null;
                                return _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsForStream2, notice =>
                                    _ = _toasts.ShowAsync(
                                        "Winpepper",
                                        "Cloud transcription unavailable — used local speech recognition instead.",
                                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                                        TimeSpan.FromSeconds(6)));
                            }, ct2),
                            _log, ct);
                    }
                    else
                    {
                        _log.LogDebug("streaming disabled by settings; batch transcription will run at stop");
                    }
                    _warmRecorder!.StartSession(includePrerollMs: 500);
                    _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _targetAtStart = CaptureTarget();

                    // PLAN2-TYPE — start window-context prefetch in parallel with audio
                    // capture. Gated on LIVE settings (not a boot snapshot) so a
                    // Cleanup-tab change applies to this dictation; prefetch is only
                    // useful when the cleanup LLM is enabled at all.
                    _ctxPrefetchTask = null;
                    var settingsAtStart2 = _settingsProvider();
                    if (_windowContext is not null
                        && settingsAtStart2.CleanupEnabled
                        && settingsAtStart2.CleanupWindowContextEnabled)
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
                        if (_streamingSession is not null)
                        {
                            var droppedStreaming2 = _streamingSession;
                            _streamingSession = null;
                            await droppedStreaming2.DisposeAsync();
                            NoteStreamingReleased(droppedStreaming2);
                        }
                        _recordStopwatch = null;
                        break;
                    }

                    var transcribeSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var settingsNow2 = _settingsProvider();
                    var cloudSelected2 = string.Equals(
                        settingsNow2.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                    Action<string> fallbackNotice2 = notice =>
                        _ = _toasts.ShowAsync(
                            "Winpepper",
                            "Cloud transcription unavailable — used local speech recognition instead.",
                            Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                            TimeSpan.FromSeconds(6));
                    // Finish the streaming session FIRST — before ANY TryEnsureAsrModel
                    // call: the ensure's swap branch disposes the ParakeetSession the
                    // streaming transcriber still holds (no engine-state gating), so
                    // no ensure may run while a session is in flight. The factory's
                    // own ensure (start arm) ran before the session captured the
                    // model. A mid-dictation model change applies to the NEXT dictation.
                    var streaming2 = _streamingSession;
                    _streamingSession = null;
                    Winpepper.Asr.Transcription.TranscriptionResult? maybeTranscription2 = null;
                    if (streaming2 is not null)
                    {
                        // Dispose the coordinator on ALL paths — including a FinishAsync
                        // throw (pump-error rethrow, inner-session throw, ct cancel):
                        // otherwise the inner streaming session (e.g. cloud websocket)
                        // leaks. DisposeAsync is idempotent, never throws, and is a
                        // near-no-op after a successful FinishAsync or a drain-timeout
                        // (both already disposed the inner session internally).
                        try
                        {
                            maybeTranscription2 = await streaming2.FinishAsync(trimmed2, ct);
                        }
                        finally
                        {
                            await streaming2.DisposeAsync();
                            // If the abandon left the pump orphaned (drain timeout /
                            // bounded dispose wait expired), register it so no model
                            // dispose can race the native call it may still be in.
                            NoteStreamingReleased(streaming2);
                        }
                    }
                    if (maybeTranscription2 is null)
                    {
                        // Late path: no streaming session materialized, its factory
                        // returned null (no provider at start), or the drain deadline
                        // expired on a wedged connection (session already abandoned +
                        // disposed inside FinishAsync). Run today's ensure + error UX,
                        // then the batch-equivalent path via the streaming seam. The
                        // cloud wrapper bounds BOTH its connect (StartSessionAsync)
                        // and its post-stop wait with the cloud deadline (Task 8), so
                        // even on the wedged network that caused a drain timeout this
                        // path cannot hang the serial hotkey loop.
                        // Provider-aware (req 6): a failed LOCAL swap never skips or
                        // aborts a CLOUD dictation; soften its error surface.
                        // Drain-timeout abandon (A5 residual, Item B): running the
                        // ensure here is safe even after a drain timeout — the
                        // abandoned pump was registered with _orphanGuard above
                        // (inside the FinishAsync finally), so the ensure's Swap
                        // branch DEFERS the old session's dispose until that pump
                        // completes instead of racing its in-flight native call.
                        var localReady2 = TryEnsureAsrModel(reportErrors: !cloudSelected2);
                        var asrNow2 = _asr;
                        if ((!localReady2 && !cloudSelected2) || asrNow2 is null)
                        {
                            if (streaming2 is not null)
                            {
                                await streaming2.DisposeAsync(); // no-op after FinishAsync; never throws
                            }
                            // Terminal-state early-exit (S2): never bare-return — drive
                            // the engine back so the next dictation can start.
                            _engine.Apply(SessionEvent.Failed);
                            if (cloudSelected2 && asrNow2 is null)
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
                        var transcriber2 = _buildTranscriber(asrNow2, _asrSwap.LoadedModelName!, settingsNow2, fallbackNotice2);
                        await using var lateSession2 = await transcriber2.StartSessionAsync(ct);
                        maybeTranscription2 = await lateSession2.FinishAsync(trimmed2, ct);
                    }
                    var transcription2 = maybeTranscription2;
                    transcribeSw2.Stop();

                    string final2 = transcription2.Text;
                    var producedModelName2 = transcription2.ProviderModelName;
                    var cleanupSw2 = new System.Diagnostics.Stopwatch();
                    var cleanupUsedModel2 = "";
                    var windowContextUsed2 = false;

                    // Live per-dictation cleanup settings: the Cleanup tab persists into
                    // AppSettings, so building options HERE (not at boot) makes the
                    // Enabled toggle and every other cleanup setting take effect on
                    // this very dictation.
                    // Per-dictation cleanup-model seam — second (toggle) path;
                    // keep byte-parallel with the hold path above.
                    var cleanupLease2 = _cleanupHolder.EnsureCurrent();
                    var cleanupRunner2 = cleanupLease2.Runner;
                    var cleanupOptions2 = Winpepper.Cleanup.CleanupOptionsFactory.FromSettings(settingsNow2);
                    var skipLlm2 = Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName2);
                    // Single policy home (CleanupRunner.Preflight) decides whether the
                    // LLM will actually run. The engine enters CleaningUp exactly for
                    // those dictations — so the pill's "Cleaning up..." phase is
                    // truthful and "Inserting..." remains reachable afterwards.
                    var llmWillRun2 = !string.IsNullOrWhiteSpace(final2) && cleanupRunner2 is not null
                        && Winpepper.Cleanup.CleanupRunner.Preflight(final2, cleanupOptions2, skipLlm2);
                    _engine.Apply(llmWillRun2 ? SessionEvent.CleanupStarted : SessionEvent.TranscriptReady);

                    if (!string.IsNullOrWhiteSpace(final2) && cleanupRunner2 is not null)
                    {
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
                            var result2 = await cleanupRunner2.RunAsync(
                                rawTranscript: final2,
                                corrections: correctionsData2,
                                windowContextTask: ctxTextTask2,
                                options: cleanupOptions2,
                                ct: ct,
                                skipLlm: skipLlm2);
                            cleanupSw2.Stop();
                            _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                                result2.Path, (int)result2.Elapsed.TotalMilliseconds);
                            final2 = result2.CleanedText;
                            cleanupUsedModel2 = result2.Path switch
                            {
                                Winpepper.Cleanup.CleanupPath.BypassProvider => "none (cloud, corrections-only)",
                                Winpepper.Cleanup.CleanupPath.BypassDisabled => "none (disabled, corrections-only)",
                                _ => cleanupLease2.LoadedModelName ?? "",
                            };
                            windowContextUsed2 = ctxTextTask2 is not null
                                                && result2.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                        }
                        catch (Exception ex)
                        {
                            cleanupSw2.Stop();
                            _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                        }

                        // Exit CleaningUp whether the runner succeeded or threw — the
                        // engine must reach Injecting either way.
                        if (llmWillRun2) _engine.Apply(SessionEvent.CleanupCompleted);
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
                            var outcome2 = _injector.TryInjectGuarded(toType2);
                            injected2 = outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
                            if (outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
                            {
                                // Focus moved to another window (or a halt-gesture
                                // modifier or mouse button went down) while the keystrokes
                                // were still going out: stop typing and hold the WHOLE
                                // transcription as a pending paste (never just the
                                // remainder -- a torn partial paste in the old window
                                // means the user re-pastes ALL of it where they want it).
                                // Not an error: no ErrorBus report, no toast, no
                                // clipboard clobbering -- the pill is the surface.
                                _vm.EnterPendingPaste(final2, _targetAtStart);
                                _log.LogInformation(
                                    "Injection interrupted (focus, modifier, or mouse-button change); held full text as pending paste ({Chars} chars)",
                                    final2.Length);
                            }
                            else if (outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
                            {
                                // The target window is elevated: UIPI silently
                                // drops SendInput while reporting success, so
                                // nothing was typed. Park the WHOLE transcription
                                // and explain via the pill copy. Not an error:
                                // no ErrorBus report, no toast.
                                _vm.EnterPendingPaste(final2, _targetAtStart,
                                    Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget);
                                _log.LogInformation(
                                    "Injection blocked: foreground window is elevated; held full text as pending paste ({Chars} chars)",
                                    final2.Length);
                            }
                            else if (outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.NoForeground)
                            {
                                // No observable foreground at send start
                                // (hwnd==0; probe-gated park polarity, council
                                // 2026-07-28): nothing was typed. Park the WHOLE
                                // transcription with the default copy. Not an
                                // error: no ErrorBus report, no toast -- the
                                // pill is the surface.
                                _vm.EnterPendingPaste(final2, _targetAtStart);
                                _log.LogInformation(
                                    "No observable foreground at injection start; held full text as pending paste ({Chars} chars)",
                                    final2.Length);
                            }
                            else if (!injected2)
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

    /// <summary>Call after permanently letting go of a streaming coordinator. If its
    /// pump was abandoned still-running, register it so no model dispose can race it.</summary>
    private void NoteStreamingReleased(Winpepper.Asr.Transcription.StreamingDictationSession s)
    {
        if (!s.PumpCompletion.IsCompleted) _orphanGuard.Register(s.PumpCompletion);
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
            // Record whether the run loop actually quiesced: AppShell disposes
            // the cleanup holder ONLY on a successful join (the wait is
            // bounded/best-effort, so "PipelineHost disposed first" alone does
            // not guarantee no generation is in flight — ledger A2). Wait()
            // throwing means _runTask is terminal (faulted/canceled), which IS
            // a completed join.
            try { RunLoopJoined = _runTask?.Wait(TimeSpan.FromSeconds(2)) ?? true; }
            catch { RunLoopJoined = true; }
            _hook.Dispose();
            // An in-flight streaming session (app shut down mid-dictation) holds a
            // live pump + inner socket; dispose it here or it leaks at teardown.
            // DisposeAsync never throws and is internally bounded, but mirror the
            // file's bounded-wait convention (_runTask above) anyway; Task.Run
            // avoids blocking on continuations posted to this (possibly UI) thread.
            // Runs BEFORE the _asr dispose below: if the bounded wait expires the
            // pump is orphaned possibly mid-native-call on _asr, so it must be
            // registered with the guard before any model dispose is routed.
            var streamingAtTeardown = _streamingSession;
            _streamingSession = null;
            if (streamingAtTeardown is not null)
            {
                try { Task.Run(() => streamingAtTeardown.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2)); } catch { }
                NoteStreamingReleased(streamingAtTeardown);
            }
            lock (_startGate)
            {
                var asrAtTeardown = _asr;
                _asr = null;
                if (asrAtTeardown is not null) _orphanGuard.RunOrDefer(asrAtTeardown.Dispose);
            }
            if (_warmRecorder is not null)
            {
                // Bug 8 (hygiene): unhook the meter + streaming-tee + fault
                // handlers before teardown.
                if (_frameHandler is not null) _warmRecorder.FramesAvailable -= _frameHandler;
                if (_streamFrameHandler is not null) _warmRecorder.FramesAvailable -= _streamFrameHandler;
                if (_captureFaultHandler is not null) _warmRecorder.CaptureFaulted -= _captureFaultHandler;
                if (_captureRecoveredHandler is not null) _warmRecorder.CaptureRecovered -= _captureRecoveredHandler;
                _warmRecorder.Dispose();
            }
        });
    }
}
#endif
