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
    private Winpepper.Asr.Transcription.IDisposableTranscriber? _asr;
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
    /// <summary>
    /// Pre-roll milliseconds the recorder ACTUALLY seeded into the current
    /// session (StartSession's return): 0 when prewarm is off, less than the
    /// PrerollRequest.ComputeRequestMs request (WarmPrerollMs + clamped
    /// hotkey lag, bounded by the previous stop hotkey) when the ring was
    /// drained/cleared. Sizes the per-dictation silence-gate cue mask
    /// (StartCueGateMask.ComputeMaskMs).
    /// Sessions are serialized by the engine state machine, so one field
    /// suffices — but BOTH hotkey arms (hold + toggle) MUST assign it;
    /// missing one arm silently reuses the other arm's stale value (no
    /// compile error catches it). The cancel path leaves it stale, which is
    /// benign: the next StartSession overwrites it before any trim reads it.
    /// </summary>
    private int _lastSessionPrerollMs;

    /// <summary>Keydown→pre-roll-seed lag (ms) of the CURRENT session — Task
    /// 4's seedLagMs, measured immediately before StartSession; &gt;= the
    /// 'Session started' line's LagMs (blocking in-arm work sits between the
    /// two). Feeds arm_latency= on the timing line. Like
    /// _lastSessionPrerollMs: BOTH arms must assign it.</summary>
    private int _lastArmLatencyMs;

    /// <summary>ms between the previous stop hotkey and this session's start
    /// hotkey when 0 &lt;= gap &lt; 3000 (the retrigger signature), else null.
    /// Read from _lastStopHotkeyUtc (field + its three stamping sites landed
    /// in Task 4 — the pre-roll bound needs the timestamp before the request
    /// math). The filter lives HERE, not in FormatLine. BOTH arms must
    /// assign it.</summary>
    private int? _retriggerGapMs;

    /// <summary>head_speech_at/head_clipped from this session's
    /// TrimForTranscription; null when trim did not run or found no clear
    /// frame outside the cue window. Reset at BOTH arms (a failed/silent
    /// session must not leak the previous session's values).</summary>
    private int? _lastHeadSpeechAtMs;
    private bool? _lastHeadClipped;
    /// <summary>Hook timestamp of the most recent stop-initiating hotkey
    /// (HoldUp / toggle-stop / Cancel); null until the first stop this
    /// process. Bounds the pre-roll request (PrerollRequest.ComputeRequestMs
    /// — the seed must never reach back past the previous stop + stop-cue
    /// guard) and is the source for retrigger_gap= (Task 7) — hook
    /// timestamps at both ends, so the gap measures USER behavior, not
    /// pipeline latency.</summary>
    private DateTimeOffset? _lastStopHotkeyUtc;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private readonly object _startGate = new();
    private Action<Exception>? _captureFaultHandler;
    private Action? _captureRecoveredHandler;
    private Action<ReadOnlyMemory<float>>? _frameHandler;
    private Winpepper.Asr.Transcription.StreamingDictationSession? _streamingSession;
    private Action<ReadOnlyMemory<float>>? _streamFrameHandler;
    // E1: routes dictations to batch while an abandoned wedged stream still
    // holds the compute gate. Hotkey-loop-only by contract.
    private readonly Winpepper.Asr.Transcription.StreamingRouteGuard _routeGuard = new();

    private readonly Winpepper.Cleanup.CleanupBackendHolder _cleanupHolder;
    // NOTE: no CleanupOptions field. Options are built PER DICTATION from the
    // settings provider (CleanupOptionsFactory.FromSettings) so Cleanup-tab
    // changes — including the Enabled toggle — take effect immediately.
    private readonly Winpepper.Corrections.CorrectionStore? _corrections; // PLAN2-TYPE
    // tbc0: prefetch launched at LISTEN-START via the sequencer (both hotkey
    // arms); lifecycle (per-dictation CTS, cancel-prior, cancel-on-drop) stays
    // owned by the coordinator. Hwnd captured at START, content now too.
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetchCoordinator? _ctxCoordinator;
    // tbc0: per-dictation listen-start launch/consume book over _ctxCoordinator
    // (null when the coordinator is absent). Both arms delegate; the sequencing
    // behavior is Linux-tested in WindowContextListenStartSequencerTests.
    private readonly Winpepper.Platform.WindowContext.WindowContextListenStartSequencer? _ctxSequencer;
    private IntPtr _ctxHwndAtStart = IntPtr.Zero;

    private readonly Winpepper.History.HistoryArchiver _archiver;
    private System.Diagnostics.Stopwatch? _recordStopwatch;
    // Dictation-window baselines for the timing line: GC deltas + prewarm
    // overlap span recording start -> emit. Safe as host fields: the run
    // loop is serial (one dictation fully processed before the next).
    private long _dictStartTicks;
    private int _gcGen0AtStart;
    private int _gcGen1AtStart;
    private int _gcGen2AtStart;
    private System.TimeSpan _gcPauseAtStart;
    private System.TimeSpan _procCpuAtStart;
    private uint? _pfAtStart;                                                            // B1
    private Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimesSample? _sysTimesAtStart; // B3
    private int? _memPrivMbAtStart;                                                      // B2
    private int? _memWsMbAtStart;
    private int? _thrAtStart;
    private int? _hndAtStart;

    private readonly Winpepper.Core.Errors.ErrorBus _errorBus;
    private Guid _currentSessionId = Guid.Empty;
    private readonly Winpepper.Platform.Injection.ClipboardFallback _clipboardFallback;
    private readonly Winpepper.Core.Notifications.IToastService _toasts;
    private readonly Func<AppSettings> _settingsProvider;
    /// <summary>Prompt format of the ACTIVE cleanup model (slot -> resolver, the
    /// same source the cleanup call uses), or null when unknown -- null behaves
    /// as today (prefetch allowed). See WindowContextPrefetchGate.</summary>
    private readonly Func<string?>? _activeCleanupPromptFormat;
    private readonly Func<Winpepper.Asr.Transcription.ITranscriber?, string?, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> _buildTranscriber;
    private readonly Func<string, string, Winpepper.Asr.Transcription.IDisposableTranscriber> _loadBatchAsr;
    private readonly Func<bool> _isPrimarySpeechReady;
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
        Func<string, string, Winpepper.Asr.Transcription.IDisposableTranscriber> loadBatchAsr,
        Func<bool> isPrimarySpeechReady,
        Winpepper.History.HistoryArchiver archiver,
        Winpepper.Cleanup.CleanupBackendHolder cleanupHolder,
        Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
        Winpepper.Core.Notifications.IToastService toasts,
        Func<AppSettings> settingsProvider,
        Func<Winpepper.Asr.Transcription.ITranscriber?, string?, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> transcriberFactory,
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null,
        bool postPasteLearningEnabled = false,
        bool prewarmMicEnabled = true,
        Func<string?>? activeCleanupPromptFormat = null)
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
        // _settingsProvider is assigned before _injector below: the ladder
        // lambda captures the FIELD (read only when TextInjector later
        // invokes channelOrder(), never during construction), but the
        // nullable-flow checker analyzes assignment order textually, so
        // assigning first here also keeps the field's flow state non-null
        // at the capture site (avoids CS8602, promoted to an error by this
        // repo's WarningsAsErrors=nullable).
        _settingsProvider = settingsProvider;
        // Ladder order is re-read per injection run (the lambda defers to
        // the settings provider), so a settings.json reorder/removal takes
        // effect without an app restart — the design's field-regression
        // recovery story. Unknown names warn-and-skip; empty/invalid lists
        // fall back to the hardcoded default inside ParseLadder.
        _injector = new TextInjector(
            factory.CreateLogger<TextInjector>(),
            channelOrder: () => InjectionChannelNames.ParseLadder(
                _settingsProvider().InjectionChannels,
                unknown => _log.LogWarning(
                    "Unknown injectionChannels entry '{Name}' in settings; skipping", unknown)));
        _resolveModelDir = resolveModelDir;
        _desiredAsrModel = desiredAsrModelName;
        _resolveAsrModelName = resolveAsrModelName;
        _isAsrModelReady = isAsrModelReady;
        _archiver = archiver;
        _cleanupHolder = cleanupHolder;
        _corrections = corrections;
        _ctxCoordinator = windowContext is null
            ? null
            : new Winpepper.Platform.WindowContext.WindowContextPrefetchCoordinator(windowContext.StartAsync);
        _ctxSequencer = _ctxCoordinator is null ? null : new(_ctxCoordinator);
        _clipboardFallback = clipboardFallback;
        _toasts = toasts;
        _buildTranscriber = transcriberFactory;
        _loadBatchAsr = loadBatchAsr;
        _isPrimarySpeechReady = isPrimarySpeechReady;
        _postPaste = postPaste;
        _focusedCapturer = focusedCapturer;
        _postPasteLearningEnabled = postPasteLearningEnabled;
        _prewarmMicEnabled = prewarmMicEnabled;
        // Startup observability for the silence-gate cue mask (2026-08-02):
        // one honest line stating what was measured and the WORST-CASE warm
        // mask, so recalibration reads of the drop log know the counting
        // basis. The mask itself varies per-dictation with the pre-roll the
        // recorder actually seeded (see TrimForTranscription); the actual
        // value is logged on each silent-drop line.
        var startCueMs = sounds.StartCueMs;
        if (startCueMs > 0)
            _log.LogInformation(
                "start cue measured {CueMs} ms; worst-case warm silence-gate cue mask {WorstCaseMaskMs} ms (max preroll request {PrerollMs} incl. hotkey-lag compensation + start latency {LatencyMs} + cue + decay {DecayMs}; per-dictation mask uses the actually-seeded preroll; sounds enabled {Enabled})",
                startCueMs,
                StartCueGateMask.ComputeMaskMs(Winpepper.Audio.PrerollRequest.MaxRequestMs, startCueMs, sounds.Enabled),
                Winpepper.Audio.PrerollRequest.MaxRequestMs,
                StartCueGateMask.CueStartLatencyMarginMs,
                StartCueGateMask.CueDecayMarginMs,
                sounds.Enabled);
        else
            _log.LogWarning(
                "start cue duration unavailable (missing or unparseable start.wav); silence-gate cue mask disabled — gate behaves as before (fail open)");
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

    /// <summary>Nemotron-first semantics: the Parakeet ONNX model is an
    /// OPTIONAL BACKUP. This method (a) loads/swaps/disposes the backup
    /// exactly as before when its files are verified-present (keep-old-on-
    /// failure, orphan-guarded dispose), but a MISSING backup is no longer an
    /// error; and (b) returns whether a LOCAL dictation can proceed at all:
    /// true when the primary streaming model is ready OR a backup session is
    /// loaded. Cloud dictations don't require it (callers pass cloudSelected).</summary>
    private bool TryEnsureAsrModel(bool reportErrors = true)
    {
        lock (_startGate)
        {
            var desired = _resolveAsrModelName(_desiredAsrModel());
            var desiredDir = _resolveModelDir(desired);
            var ready = _isAsrModelReady(desired);
            var action = _asrSwap.Plan(desired, ready);

            switch (action)
            {
                case Winpepper.Core.Asr.AsrSwapAction.KeepCurrent:
                    break;

                case Winpepper.Core.Asr.AsrSwapAction.CannotStart:
                    // Backup not installed/verified: fine — Nemotron is primary.
                    _log.LogDebug("backup ASR model {Model} not verified-ready in {ModelDir}; continuing without a backup",
                        desired, desiredDir);
                    break;

                case Winpepper.Core.Asr.AsrSwapAction.Load:
                case Winpepper.Core.Asr.AsrSwapAction.Swap:
                    try
                    {
                        var previousModel = _asrSwap.LoadedModelName;
                        var fresh = _loadBatchAsr(desiredDir, desired);
                        var old = _asr;
                        _asr = fresh;
                        _asrSwap.CommitLoad(desired);
                        // Under _startGate; idempotent. Routed through the orphan
                        // guard: an abandoned streaming pump may still be executing
                        // a native call on the old session (RunOrDefer never blocks).
                        if (old is not null) _orphanGuard.RunOrDefer(old.Dispose);
                        _log.LogInformation(
                            "backup ASR model loaded (swap #{Generation}): {Previous} -> {Model}",
                            _asrSwap.Generation, previousModel ?? "(none)", desired);
                        _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Asr);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Failed to load backup ASR model {Model} from {ModelDir}; keeping previous session",
                            desired, desiredDir);
                        if (reportErrors)
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
                        // keep-old-on-failure; fall through to primary check
                    }
                    break;
            }

            // A LOCAL dictation needs at least one of: primary streaming model
            // ready, or a loaded backup session.
            if (_asr is not null || _isPrimarySpeechReady()) return true;

            _log.LogWarning("no local speech model available (primary not installed, no backup loaded)");
            if (reportErrors)
            {
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                    new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                    Guid.Empty);
            }
            return false;
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

    /// <summary>tbc0: evaluate the listen-start policy on the start-time settings
    /// snapshot and delegate the launch (+book) to the sequencer. Call ONLY after
    /// _ctxCoordinator?.OnRecordingStart() and the _ctxHwndAtStart capture. The
    /// single launch LogInformation lives here (one site, R1 evidence).</summary>
    private void LaunchPrefetchAtListenStart(AppSettings settingsAtStart)
    {
        if (_ctxSequencer is null) return;
        var handle = _ctxSequencer.RecordingStarted(
            Winpepper.Cleanup.WindowContextListenStartPolicy.ShouldStart(
                settingsAtStart.CleanupEnabled,
                settingsAtStart.CleanupWindowContextEnabled,
                _activeCleanupPromptFormat?.Invoke(),
                _ctxHwndAtStart != IntPtr.Zero),
            _ctxHwndAtStart);
        if (handle is not null)
            _log.LogInformation("window-context prefetch started at listen-start {SessionId}", _currentSessionId);
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
        Winpepper.Platform.Injection.InjectionRunReport report = default;
        Winpepper.Platform.Injection.InjectionRunOutcome outcome;
        if (string.IsNullOrWhiteSpace(text))
        {
            outcome = Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed; // never ran
        }
        else
        {
            report = _injector.TryInjectGuardedDetailed(text);
            outcome = report.Outcome;
        }
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
            _log.LogInformation(
                "Pending paste injected ({Chars} chars, {ChunksSent}/{ChunksTotal} chunks, nominal pacing {PacingMs} ms, via {Via})",
                text.Length, report.ChunksSent, report.ChunksTotal, report.PacingWaitMs,
                InjectionChannelNames.Name(report.Via));
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
                // Hoisted keydown→handling lag: feeds ONLY the 'Session
                // started' log line (semantics unchanged — stays comparable
                // with the historical lag survey). The pre-roll request and
                // arm_latency= use seedLagMs, measured immediately before
                // StartSession below (blocking work sits in between).
                var hotkeyLagMs = (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds;
                _log.LogInformation("Session started (hold) {SessionId} (hotkey observed {LagMs} ms before handling)",
                    _currentSessionId, hotkeyLagMs);
                _retriggerGapMs = null;
                if (_lastStopHotkeyUtc is DateTimeOffset prevStopAt)
                {
                    // Hook-time to hook-time: immune to handler serialization.
                    // Negative (wall-clock skew) or >= 3 s gaps are not
                    // retriggers — omit the field entirely.
                    var gapMs = (int)(evt.Timestamp - prevStopAt).TotalMilliseconds;
                    if (gapMs is >= 0 and < 3000) _retriggerGapMs = gapMs;
                }
                _sounds.PlayStart();
                // Start the streaming dictation session BEFORE StartSession —
                // StartSession raises the lag-compensated pre-roll request
                // (PrerollRequest.ComputeRequestMs: WarmPrerollMs 1000 ms +
                // observed hotkey lag, clamped) synchronously through
                // FramesAvailable, so the session must already exist (frames
                // queue in the coordinator until the factory completes) or the
                // cloud stream permanently loses the first ~1-2 s. The factory
                // runs on a background pump so recording start stays instant;
                // model ensure is silent here (reportErrors: false) — the stop
                // arm's late path re-runs the check with today's exact error UX.
                var settingsForStream = _settingsProvider();
                if (settingsForStream.StreamingEnabled)
                {
                    if (_routeGuard.TryClaimStreaming(out var routeBlockReason))
                    {
                        _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                            ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                            {
                                var cloudSel = string.Equals(settingsForStream.AsrProvider, "assemblyai",
                                    StringComparison.OrdinalIgnoreCase);
                                var ready = TryEnsureAsrModel(reportErrors: false);
                                if (!ready && !cloudSel) return null;
                                return _buildTranscriber(_asr, _asrSwap.LoadedModelName, settingsForStream, notice =>
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
                        // E1: leave _streamingSession null — the existing
                        // late batch path takes over at stop, same as when
                        // streaming is disabled by settings.
                        _log.LogInformation(
                            "streaming routed to batch for this dictation: {Reason}", routeBlockReason);
                    }
                }
                else
                {
                    _log.LogDebug("streaming disabled by settings; batch transcription will run at stop");
                }
                // Measured adjacent to the seed, NOT reused from the arm top:
                // blocking work sits between the two (synchronous settings-
                // file Load with a documented 15 ms×2 collision-sleep path,
                // SettingsStore.cs:24-63, plus 1-2 sync Serilog file-sink
                // writes, WinpepperLogging.cs:46-64 — falsified 2026-08-04,
                // load-bearing validation). The REQUEST and arm_latency= use
                // seedLagMs so compensation covers the whole keydown→seed
                // delay 1:1.
                var seedLagMs = (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds;
                // Bound the reach-back at the previous stop hotkey: the ring
                // is continuous across sessions, so an unbounded request
                // would hand this session the previous dictation's tail
                // (already transcribed) and its stop-beep pickup (A1/A6,
                // 2026-08-04). Negative msSinceStop (clock skew) is fine:
                // Math.Max(0, …) in the helper clamps the clean span to 0,
                // which is the conservative direction.
                int? msSinceStop = _lastStopHotkeyUtc is DateTimeOffset prevStop
                    ? (int)(DateTimeOffset.UtcNow - prevStop).TotalMilliseconds
                    : null;
                _lastSessionPrerollMs = _warmRecorder!.StartSession(
                    includePrerollMs: PrerollRequest.ComputeRequestMs(
                        seedLagMs, msSinceStop, _sounds.Enabled));
                _lastArmLatencyMs = seedLagMs; // Task 4's seed-adjacent lag, NOT the arm-top hotkeyLagMs
                _lastHeadSpeechAtMs = null;
                _lastHeadClipped = null;
                _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                _dictStartTicks = Environment.TickCount64;
                _gcGen0AtStart = GC.CollectionCount(0);
                _gcGen1AtStart = GC.CollectionCount(1);
                _gcGen2AtStart = GC.CollectionCount(2);
                _gcPauseAtStart = GC.GetTotalPauseDuration();
                var procAtStart = System.Diagnostics.Process.GetCurrentProcess();
                _procCpuAtStart = procAtStart.TotalProcessorTime;
                // B2: point-in-time resource snapshot at recording start.
                _memPrivMbAtStart = (int)(procAtStart.PrivateMemorySize64 / (1024 * 1024));
                _memWsMbAtStart = (int)(procAtStart.WorkingSet64 / (1024 * 1024));
                _thrAtStart = procAtStart.Threads.Count;
                _hndAtStart = procAtStart.HandleCount;
                // B1/B3: baselines for stop-time deltas.
                _pfAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount();
                _sysTimesAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes();
                _targetAtStart = CaptureTarget();

                // 1a ruling: a prior dictation's still-running prefetch dies
                // the moment new speech starts — live speech wins over a
                // stale context fetch; the prior dictation stamps
                // ctx_src=none, an accepted counted loss.
                _ctxCoordinator?.OnRecordingStart();
                // 1a(b): capture the dictated-into window NOW; the listen-start
                // prefetch reads THIS window, not whatever has focus by then.
                _ctxHwndAtStart = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
                // tbc0: launch at listen-start (supersedes 1a's stop-launch) — see LaunchPrefetchAtListenStart.
                LaunchPrefetchAtListenStart(settingsForStream);
                break;
            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                _recordStopwatch?.Stop();
                var procCpuAtStop = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

                var releaseAt = evt.Timestamp;
                _lastStopHotkeyUtc = evt.Timestamp;
                var timing = new Winpepper.Core.Diagnostics.DictationTimingSummary
                {
                    SessionId = _currentSessionId,
                    Kind = "hold",
                };
                timing.RecordMs = (int?)_recordStopwatch?.ElapsedMilliseconds;
                timing.ProcCpuMs = (int)(procCpuAtStop - _procCpuAtStart).TotalMilliseconds;
                timing.MemPrivMb = _memPrivMbAtStart;
                timing.MemWsMb = _memWsMbAtStart;
                timing.ThreadCount = _thrAtStart;
                timing.HandleCount = _hndAtStart;
                if (_pfAtStart is uint pf0
                    && Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount() is uint pf1)
                    timing.PageFaults = (int)(pf1 - pf0);
                if (_sysTimesAtStart is { } st0
                    && Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes() is { } st1)
                    timing.SysCpuPct = Winpepper.Core.Diagnostics.DictationTimingSummary.SystemCpuPercent(
                        st1.Idle100ns - st0.Idle100ns,
                        st1.Kernel100ns - st0.Kernel100ns,
                        st1.User100ns - st0.User100ns);

                // tbc0: the prefetch was launched at listen-start (start arm); consume it here.
                var ctxPrefetch = _ctxSequencer?.RecordingStopped();

                var micStopSw = System.Diagnostics.Stopwatch.StartNew();
                var samples = _warmRecorder!.StopSession();
                micStopSw.Stop();
                timing.MicStopMs = (int)micStopSw.ElapsedMilliseconds;
                WarnIfSessionSilent(samples, _currentSessionId);
                _sounds.PlayStop();

                var trimSw = System.Diagnostics.Stopwatch.StartNew();
                var trimmed = TrimForTranscription(samples, _currentSessionId, out var trimRemovedMs);
                trimSw.Stop();
                timing.TrimMs = (int)trimSw.ElapsedMilliseconds;
                if (trimRemovedMs > 0) timing.TrimRemovedMs = trimRemovedMs;
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
                        IsSilentDrop = true,
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
                    _ctxCoordinator?.CancelAndClear();
                    _ctxSequencer?.Clear();
                    if (_streamingSession is not null)
                    {
                        var droppedStreaming = _streamingSession;
                        _streamingSession = null;
                        await droppedStreaming.DisposeAsync();
                        NoteStreamingReleased(droppedStreaming);
                        // E1 coverage gap: cancel/silence-drop/teardown orphan a
                        // wedged pump after ~5 s with DrainTimedOut still false; a
                        // gate-holding orphan must still arm the batch routing or the
                        // cascade re-enters via cancel.
                        _routeGuard.NoteDisposeOutcome(
                            droppedStreaming.DrainTimedOut, droppedStreaming.PumpCompletion);
                    }
                    _recordStopwatch = null;
                    timing.Outcome = "silent";
                    timing.TotalMs = TotalSince(releaseAt);
                    EmitTimingSummary(timing);
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
                // call: the ensure's swap branch disposes the backup session the
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
                        // E1: a drain-timeout abandon leaves the wedged pump
                        // holding the compute gate via the queued dispose —
                        // route later dictations to batch until it completes.
                        // NoteDisposeOutcome also catches the orphan case
                        // (pump still incomplete with DrainTimedOut false).
                        _routeGuard.NoteDisposeOutcome(
                            streaming.DrainTimedOut, streaming.PumpCompletion);
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
                    if (!localReady && !cloudSelected)
                    {
                        if (streaming is not null)
                        {
                            await streaming.DisposeAsync(); // no-op after FinishAsync; never throws
                        }
                        // Terminal-state early-exit (S2): never bare-return — drive
                        // the engine back so the next dictation can start.
                        _engine.Apply(SessionEvent.Failed);
                        _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                        timing.Outcome = "failed";
                        timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;
                        StampStreamingFinishStats(timing, streaming, _dictStartTicks);
                        timing.AsrMode = "batch";
                        timing.TotalMs = TotalSince(releaseAt);
                        EmitTimingSummary(timing);
                        return;
                    }
                    var transcriber = _buildTranscriber(asrNow, _asrSwap.LoadedModelName, settingsNow, fallbackNotice);
                    await using var lateSession = await transcriber.StartSessionAsync(ct);
                    maybeTranscription = await lateSession.FinishAsync(trimmed, ct);
                }
                var transcription = maybeTranscription;
                transcribeSw.Stop();

                string final = transcription.Text;
                var producedModelName = transcription.ProviderModelName;
                timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;
                StampStreamingFinishStats(timing, streaming, _dictStartTicks);
                // Streaming iff the produced name IS a known streaming layout name.
                // For() maps unknown names to English, so only exact streaming names
                // classify as streaming; "-batch" names stay batch.
                var isStreaming = Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(producedModelName).Name == producedModelName;
                timing.AsrMode =
                    isStreaming ? "streaming"
                    : Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName) ? "cloud"
                    : "batch";
                timing.AsrModel = producedModelName;
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

                // Load corrections REGARDLESS of whether a cleanup runner is
                // live: deterministic corrections must apply even when the
                // cleanup LLM backend is unavailable (boot pre-warm race,
                // model missing, hash-verify failure) or the runner throws.
                var correctionsSw = System.Diagnostics.Stopwatch.StartNew();
                var correctionsData = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;
                correctionsSw.Stop();
                timing.CorrectionsMs = (int)correctionsSw.ElapsedMilliseconds;

                if (!string.IsNullOrWhiteSpace(final) && cleanupRunner is not null)
                {
                    // Plan 2's CleanupRunner.RunAsync expects a Task<string?>? for the
                    // window context. Adapt our Task<WindowContextResult> by projecting
                    // .Text out (or null on failure). This mirrors Plan 2 Cli/Pipeline.cs
                    // lines 3749-3751.
                    Task<string?>? ctxTextTask = null;
                    if (ctxPrefetch is not null)
                    {
                        ctxTextTask = ctxPrefetch.Task.ContinueWith(
                            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                            ct,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

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
                        timing.CleanupMs = (int)cleanupSw.ElapsedMilliseconds;
                        timing.CleanupPath = result.Path.ToString();
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                        cleanupUsedModel = result.Path switch
                        {
                            Winpepper.Cleanup.CleanupPath.BypassProvider => "none (cloud, corrections-only)",
                            Winpepper.Cleanup.CleanupPath.BypassDisabled => "none (disabled, corrections-only)",
                            _ => cleanupLease.LoadedModelName ?? "",
                        };
                        timing.CleanupModel = string.IsNullOrWhiteSpace(cleanupUsedModel) ? null : cleanupUsedModel;
                        windowContextUsed = ctxTextTask is not null
                                            && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                        // 0b consume-time ctx_src, via the pure Linux-tested
                        // stamp (see WindowContextStamp).
                        timing.CtxSrc = Winpepper.Platform.WindowContext.WindowContextStamp.CtxSrc(
                            result.ConsumedWindowContext, ctxPrefetch?.Task);
                        // tbc0: ≈0 once the prefetch launches at listen-start
                        timing.CtxWaitMs = result.WindowContextWaitMs;
                    }
                    catch (Exception ex)
                    {
                        cleanupSw.Stop();
                        timing.CleanupMs = (int)cleanupSw.ElapsedMilliseconds;
                        timing.CleanupPath = "exception";
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                        // A thrown cleanup run must still yield corrected raw
                        // text — corrections are deterministic and independent
                        // of the LLM.
                        final = Winpepper.Cleanup.CleanupRunner.ApplyCorrectionsOnly(final, correctionsData);
                    }

                    // Exit CleaningUp whether the runner succeeded or threw — the
                    // engine must reach Injecting either way.
                    if (llmWillRun) _engine.Apply(SessionEvent.CleanupCompleted);
                }
                else if (!string.IsNullOrWhiteSpace(final))
                {
                    // No cleanup runner is live (boot pre-warm race, model
                    // missing, hash-verify failure): the LLM cannot run, but
                    // deterministic corrections still must.
                    final = Winpepper.Cleanup.CleanupRunner.ApplyCorrectionsOnly(final, correctionsData);
                }

                // injectSw is WALL time: it includes CaptureTarget() and up to
                // 2 x 1500 ms release-wait preludes inside TryInjectGuardedDetailed
                // -- inject_pace (nominal) vs inject (wall) separates pacing from
                // prelude stalls.
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
                        var injReport = _injector.TryInjectGuardedDetailed(toType);
                        var outcome = injReport.Outcome;
                        if (injReport.ChunksTotal > 0)
                        {
                            timing.InjectChunksSent = injReport.ChunksSent;
                            timing.InjectChunksTotal = injReport.ChunksTotal;
                            timing.InjectPacingMs = injReport.PacingWaitMs;
                            timing.InjectVia = InjectionChannelNames.Name(injReport.Via);
                            if (!string.IsNullOrEmpty(injReport.GatesSummary))
                                timing.InjectGates = injReport.GatesSummary;
                        }
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
                timing.InjectMs = (int)injectSw.ElapsedMilliseconds;
                if (!string.IsNullOrWhiteSpace(final)) timing.InjectChars = final.Length;
                // Outcome derivation: "pending" covers every park reason
                // (HoldPending, Interrupted, BlockedElevated, NoForeground,
                // SendFailed -- all end in EnterPendingPaste). "empty" is the
                // honest bucket for the empty-final-text dictation where the
                // whole injection block was skipped: no injection ran and no
                // pending paste exists, so neither "completed" nor "pending"
                // would be true.
                timing.Outcome = injected
                    ? "completed"
                    : (string.IsNullOrWhiteSpace(final) ? "empty" : "pending");
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

                // Emit BEFORE Archive: an Archive throw (escapes to the RunAsync
                // catch) can never skip the timing line.
                timing.TotalMs = TotalSince(releaseAt);
                EmitTimingSummary(timing);

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

                // prefetch handle is per-dictation (local); the coordinator's reference is
                // cleared by the next OnRecordingStart.
                _recordStopwatch = null;
                break;
            case HotkeyEventKind.Cancel:
                _lastStopHotkeyUtc = evt.Timestamp;
                _engine.Apply(SessionEvent.CancelRequested);
                _ctxCoordinator?.CancelAndClear();
                _ctxSequencer?.Clear();
                _log.LogInformation("Session cancelled {SessionId}", _currentSessionId);
                _ = _warmRecorder?.StopSession();
                if (_streamingSession is not null)
                {
                    var cancelledStreaming = _streamingSession;
                    _streamingSession = null;
                    await cancelledStreaming.DisposeAsync();
                    NoteStreamingReleased(cancelledStreaming);
                    // E1 coverage gap: cancel/silence-drop/teardown orphan a
                    // wedged pump after ~5 s with DrainTimedOut still false; a
                    // gate-holding orphan must still arm the batch routing or the
                    // cascade re-enters via cancel.
                    _routeGuard.NoteDisposeOutcome(
                        cancelledStreaming.DrainTimedOut, cancelledStreaming.PumpCompletion);
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
                    // Hoisted keydown→handling lag: feeds ONLY the 'Session
                    // started' log line (semantics unchanged — stays comparable
                    // with the historical lag survey). The pre-roll request and
                    // arm_latency= use seedLagMs2, measured immediately before
                    // StartSession below (blocking work sits in between).
                    var hotkeyLagMs2 = (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds;
                    _log.LogInformation("Session started (toggle) {SessionId} (hotkey observed {LagMs} ms before handling)",
                        _currentSessionId, hotkeyLagMs2);
                    _retriggerGapMs = null;
                    if (_lastStopHotkeyUtc is DateTimeOffset prevStopAt2)
                    {
                        // Hook-time to hook-time: immune to handler serialization.
                        // Negative (wall-clock skew) or >= 3 s gaps are not
                        // retriggers — omit the field entirely.
                        var gapMs2 = (int)(evt.Timestamp - prevStopAt2).TotalMilliseconds;
                        if (gapMs2 is >= 0 and < 3000) _retriggerGapMs = gapMs2;
                    }
                    _sounds.PlayStart();
                    // (same comment as the HoldDown arm: create BEFORE StartSession
                    // so the synchronously-raised lag-compensated pre-roll request
                    // (PrerollRequest.ComputeRequestMs: WarmPrerollMs 1000 ms +
                    // observed hotkey lag, clamped) is not dropped)
                    var settingsForStream2 = _settingsProvider();
                    if (settingsForStream2.StreamingEnabled)
                    {
                        if (_routeGuard.TryClaimStreaming(out var routeBlockReason2))
                        {
                            _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                                ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                                {
                                    var cloudSel2 = string.Equals(settingsForStream2.AsrProvider, "assemblyai",
                                        StringComparison.OrdinalIgnoreCase);
                                    var ready2 = TryEnsureAsrModel(reportErrors: false);
                                    if (!ready2 && !cloudSel2) return null;
                                    return _buildTranscriber(_asr, _asrSwap.LoadedModelName, settingsForStream2, notice =>
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
                            // E1: leave _streamingSession null — the existing
                            // late batch path takes over at stop, same as when
                            // streaming is disabled by settings.
                            _log.LogInformation(
                                "streaming routed to batch for this dictation: {Reason}", routeBlockReason2);
                        }
                    }
                    else
                    {
                        _log.LogDebug("streaming disabled by settings; batch transcription will run at stop");
                    }
                    // Measured adjacent to the seed, NOT reused from the arm top:
                    // blocking work sits between the two (synchronous settings-
                    // file Load with a documented 15 ms×2 collision-sleep path,
                    // SettingsStore.cs:24-63, plus 1-2 sync Serilog file-sink
                    // writes, WinpepperLogging.cs:46-64 — falsified 2026-08-04,
                    // load-bearing validation). The REQUEST and arm_latency= use
                    // seedLagMs2 so compensation covers the whole keydown→seed
                    // delay 1:1.
                    var seedLagMs2 = (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds;
                    // Bound the reach-back at the previous stop hotkey: the ring
                    // is continuous across sessions, so an unbounded request
                    // would hand this session the previous dictation's tail
                    // (already transcribed) and its stop-beep pickup (A1/A6,
                    // 2026-08-04). Negative msSinceStop2 (clock skew) is fine:
                    // Math.Max(0, …) in the helper clamps the clean span to 0,
                    // which is the conservative direction.
                    int? msSinceStop2 = _lastStopHotkeyUtc is DateTimeOffset prevStop2
                        ? (int)(DateTimeOffset.UtcNow - prevStop2).TotalMilliseconds
                        : null;
                    _lastSessionPrerollMs = _warmRecorder!.StartSession(
                        includePrerollMs: PrerollRequest.ComputeRequestMs(
                            seedLagMs2, msSinceStop2, _sounds.Enabled));
                    _lastArmLatencyMs = seedLagMs2; // Task 4's seed-adjacent lag, NOT the arm-top hotkeyLagMs2
                    _lastHeadSpeechAtMs = null;
                    _lastHeadClipped = null;
                    _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    _dictStartTicks = Environment.TickCount64;
                    _gcGen0AtStart = GC.CollectionCount(0);
                    _gcGen1AtStart = GC.CollectionCount(1);
                    _gcGen2AtStart = GC.CollectionCount(2);
                    _gcPauseAtStart = GC.GetTotalPauseDuration();
                    var procAtStart2 = System.Diagnostics.Process.GetCurrentProcess();
                    _procCpuAtStart = procAtStart2.TotalProcessorTime;
                    // B2: point-in-time resource snapshot at recording start.
                    _memPrivMbAtStart = (int)(procAtStart2.PrivateMemorySize64 / (1024 * 1024));
                    _memWsMbAtStart = (int)(procAtStart2.WorkingSet64 / (1024 * 1024));
                    _thrAtStart = procAtStart2.Threads.Count;
                    _hndAtStart = procAtStart2.HandleCount;
                    // B1/B3: baselines for stop-time deltas.
                    _pfAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount();
                    _sysTimesAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes();
                    _targetAtStart = CaptureTarget();

                    // 1a ruling: a prior dictation's still-running prefetch dies
                    // the moment new speech starts — live speech wins over a
                    // stale context fetch; the prior dictation stamps
                    // ctx_src=none, an accepted counted loss.
                    _ctxCoordinator?.OnRecordingStart();
                    // 1a(b): capture the dictated-into window NOW; the listen-start
                    // prefetch reads THIS window, not whatever has focus by then.
                    _ctxHwndAtStart = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
                    // tbc0: launch at listen-start (supersedes 1a's stop-launch) — see LaunchPrefetchAtListenStart.
                    LaunchPrefetchAtListenStart(settingsForStream2);
                }
                else if (_engine.State == SessionState.Recording)
                {
                    _engine.Apply(SessionEvent.StopRequested);
                    _recordStopwatch?.Stop();
                    var procCpuAtStop2 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;

                    var releaseAt2 = evt.Timestamp;
                    _lastStopHotkeyUtc = evt.Timestamp;
                    var timing2 = new Winpepper.Core.Diagnostics.DictationTimingSummary
                    {
                        SessionId = _currentSessionId,
                        Kind = "toggle",
                    };
                    timing2.RecordMs = (int?)_recordStopwatch?.ElapsedMilliseconds;
                    timing2.ProcCpuMs = (int)(procCpuAtStop2 - _procCpuAtStart).TotalMilliseconds;
                    timing2.MemPrivMb = _memPrivMbAtStart;
                    timing2.MemWsMb = _memWsMbAtStart;
                    timing2.ThreadCount = _thrAtStart;
                    timing2.HandleCount = _hndAtStart;
                    if (_pfAtStart is uint pf0_2
                        && Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount() is uint pf1_2)
                        timing2.PageFaults = (int)(pf1_2 - pf0_2);
                    if (_sysTimesAtStart is { } st0_2
                        && Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes() is { } st1_2)
                        timing2.SysCpuPct = Winpepper.Core.Diagnostics.DictationTimingSummary.SystemCpuPercent(
                            st1_2.Idle100ns - st0_2.Idle100ns,
                            st1_2.Kernel100ns - st0_2.Kernel100ns,
                            st1_2.User100ns - st0_2.User100ns);

                    // tbc0: the prefetch was launched at listen-start (start arm); consume it here.
                    var ctxPrefetch2 = _ctxSequencer?.RecordingStopped();

                    var micStopSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var samples2 = _warmRecorder!.StopSession();
                    micStopSw2.Stop();
                    timing2.MicStopMs = (int)micStopSw2.ElapsedMilliseconds;
                    WarnIfSessionSilent(samples2, _currentSessionId);
                    _sounds.PlayStop();

                    var trimSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var trimmed2 = TrimForTranscription(samples2, _currentSessionId, out var trimRemovedMs2);
                    trimSw2.Stop();
                    timing2.TrimMs = (int)trimSw2.ElapsedMilliseconds;
                    if (trimRemovedMs2 > 0) timing2.TrimRemovedMs = trimRemovedMs2;
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
                            IsSilentDrop = true,
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
                        _ctxCoordinator?.CancelAndClear();
                        _ctxSequencer?.Clear();
                        if (_streamingSession is not null)
                        {
                            var droppedStreaming2 = _streamingSession;
                            _streamingSession = null;
                            await droppedStreaming2.DisposeAsync();
                            NoteStreamingReleased(droppedStreaming2);
                            // E1 coverage gap: cancel/silence-drop/teardown orphan a
                            // wedged pump after ~5 s with DrainTimedOut still false; a
                            // gate-holding orphan must still arm the batch routing or the
                            // cascade re-enters via cancel.
                            _routeGuard.NoteDisposeOutcome(
                                droppedStreaming2.DrainTimedOut, droppedStreaming2.PumpCompletion);
                        }
                        _recordStopwatch = null;
                        timing2.Outcome = "silent";
                        timing2.TotalMs = TotalSince(releaseAt2);
                        EmitTimingSummary(timing2);
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
                    // call: the ensure's swap branch disposes the backup session the
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
                            // E1: a drain-timeout abandon leaves the wedged pump
                            // holding the compute gate via the queued dispose —
                            // route later dictations to batch until it completes.
                            // NoteDisposeOutcome also catches the orphan case
                            // (pump still incomplete with DrainTimedOut false).
                            _routeGuard.NoteDisposeOutcome(
                                streaming2.DrainTimedOut, streaming2.PumpCompletion);
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
                        if (!localReady2 && !cloudSelected2)
                        {
                            if (streaming2 is not null)
                            {
                                await streaming2.DisposeAsync(); // no-op after FinishAsync; never throws
                            }
                            // Terminal-state early-exit (S2): never bare-return — drive
                            // the engine back so the next dictation can start.
                            _engine.Apply(SessionEvent.Failed);
                            _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                            timing2.Outcome = "failed";
                            timing2.AsrMs = (int)transcribeSw2.ElapsedMilliseconds;
                            StampStreamingFinishStats(timing2, streaming2, _dictStartTicks);
                            timing2.AsrMode = "batch";
                            timing2.TotalMs = TotalSince(releaseAt2);
                            EmitTimingSummary(timing2);
                            return;
                        }
                        var transcriber2 = _buildTranscriber(asrNow2, _asrSwap.LoadedModelName, settingsNow2, fallbackNotice2);
                        await using var lateSession2 = await transcriber2.StartSessionAsync(ct);
                        maybeTranscription2 = await lateSession2.FinishAsync(trimmed2, ct);
                    }
                    var transcription2 = maybeTranscription2;
                    transcribeSw2.Stop();

                    string final2 = transcription2.Text;
                    var producedModelName2 = transcription2.ProviderModelName;
                    timing2.AsrMs = (int)transcribeSw2.ElapsedMilliseconds;
                    StampStreamingFinishStats(timing2, streaming2, _dictStartTicks);
                    // Streaming iff the produced name IS a known streaming layout name.
                    // For() maps unknown names to English, so only exact streaming names
                    // classify as streaming; "-batch" names stay batch.
                    var isStreaming2 = Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(producedModelName2).Name == producedModelName2;
                    timing2.AsrMode =
                        isStreaming2 ? "streaming"
                        : Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName2) ? "cloud"
                        : "batch";
                    timing2.AsrModel = producedModelName2;
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

                    // Load corrections REGARDLESS of whether a cleanup runner is
                    // live: deterministic corrections must apply even when the
                    // cleanup LLM backend is unavailable (boot pre-warm race,
                    // model missing, hash-verify failure) or the runner throws.
                    var correctionsSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var correctionsData2 = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;
                    correctionsSw2.Stop();
                    timing2.CorrectionsMs = (int)correctionsSw2.ElapsedMilliseconds;

                    if (!string.IsNullOrWhiteSpace(final2) && cleanupRunner2 is not null)
                    {
                        // Plan 2's CleanupRunner.RunAsync expects a Task<string?>? for the
                        // window context. Adapt our Task<WindowContextResult> by projecting
                        // .Text out (or null on failure). This mirrors Plan 2 Cli/Pipeline.cs
                        // lines 3749-3751.
                        Task<string?>? ctxTextTask2 = null;
                        if (ctxPrefetch2 is not null)
                        {
                            ctxTextTask2 = ctxPrefetch2.Task.ContinueWith(
                                t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                                ct,
                                TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                        }

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
                            timing2.CleanupMs = (int)cleanupSw2.ElapsedMilliseconds;
                            timing2.CleanupPath = result2.Path.ToString();
                            _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                                result2.Path, (int)result2.Elapsed.TotalMilliseconds);
                            final2 = result2.CleanedText;
                            cleanupUsedModel2 = result2.Path switch
                            {
                                Winpepper.Cleanup.CleanupPath.BypassProvider => "none (cloud, corrections-only)",
                                Winpepper.Cleanup.CleanupPath.BypassDisabled => "none (disabled, corrections-only)",
                                _ => cleanupLease2.LoadedModelName ?? "",
                            };
                            timing2.CleanupModel = string.IsNullOrWhiteSpace(cleanupUsedModel2) ? null : cleanupUsedModel2;
                            windowContextUsed2 = ctxTextTask2 is not null
                                                && result2.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                            // 0b consume-time ctx_src, via the pure Linux-tested
                            // stamp (see WindowContextStamp).
                            timing2.CtxSrc = Winpepper.Platform.WindowContext.WindowContextStamp.CtxSrc(
                                result2.ConsumedWindowContext, ctxPrefetch2?.Task);
                            // tbc0: ≈0 once the prefetch launches at listen-start
                            timing2.CtxWaitMs = result2.WindowContextWaitMs;
                        }
                        catch (Exception ex)
                        {
                            cleanupSw2.Stop();
                            timing2.CleanupMs = (int)cleanupSw2.ElapsedMilliseconds;
                            timing2.CleanupPath = "exception";
                            _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                            // A thrown cleanup run must still yield corrected raw
                            // text — corrections are deterministic and independent
                            // of the LLM.
                            final2 = Winpepper.Cleanup.CleanupRunner.ApplyCorrectionsOnly(final2, correctionsData2);
                        }

                        // Exit CleaningUp whether the runner succeeded or threw — the
                        // engine must reach Injecting either way.
                        if (llmWillRun2) _engine.Apply(SessionEvent.CleanupCompleted);
                    }
                    else if (!string.IsNullOrWhiteSpace(final2))
                    {
                        // No cleanup runner is live (boot pre-warm race, model
                        // missing, hash-verify failure): the LLM cannot run, but
                        // deterministic corrections still must.
                        final2 = Winpepper.Cleanup.CleanupRunner.ApplyCorrectionsOnly(final2, correctionsData2);
                    }

                    // injectSw2 is WALL time: it includes CaptureTarget() and up to
                    // 2 x 1500 ms release-wait preludes inside TryInjectGuardedDetailed
                    // -- inject_pace (nominal) vs inject (wall) separates pacing from
                    // prelude stalls.
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
                            var injReport2 = _injector.TryInjectGuardedDetailed(toType2);
                            var outcome2 = injReport2.Outcome;
                            if (injReport2.ChunksTotal > 0)
                            {
                                timing2.InjectChunksSent = injReport2.ChunksSent;
                                timing2.InjectChunksTotal = injReport2.ChunksTotal;
                                timing2.InjectPacingMs = injReport2.PacingWaitMs;
                                timing2.InjectVia = InjectionChannelNames.Name(injReport2.Via);
                                if (!string.IsNullOrEmpty(injReport2.GatesSummary))
                                    timing2.InjectGates = injReport2.GatesSummary;
                            }
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
                    timing2.InjectMs = (int)injectSw2.ElapsedMilliseconds;
                    if (!string.IsNullOrWhiteSpace(final2)) timing2.InjectChars = final2.Length;
                    // Outcome derivation: "pending" covers every park reason
                    // (HoldPending, Interrupted, BlockedElevated, NoForeground,
                    // SendFailed -- all end in EnterPendingPaste). "empty" is the
                    // honest bucket for the empty-final-text dictation where the
                    // whole injection block was skipped: no injection ran and no
                    // pending paste exists, so neither "completed" nor "pending"
                    // would be true.
                    timing2.Outcome = injected2
                        ? "completed"
                        : (string.IsNullOrWhiteSpace(final2) ? "empty" : "pending");
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

                    // Emit BEFORE Archive: an Archive throw (escapes to the RunAsync
                    // catch) can never skip the timing line.
                    timing2.TotalMs = TotalSince(releaseAt2);
                    EmitTimingSummary(timing2);

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

                    // prefetch handle is per-dictation (local); the coordinator's reference is
                    // cleared by the next OnRecordingStart.
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

    /// <summary>Copy the streaming coordinator's out-of-band finish metrics
    /// onto the timing summary (asr_wait/asr_native split of asr=, queued
    /// backlog, native-call aggregates). Null-safe: no-op when streaming
    /// never existed or FinishAsync never ran.</summary>
    private static void StampStreamingFinishStats(
        Winpepper.Core.Diagnostics.DictationTimingSummary timing,
        Winpepper.Asr.Transcription.StreamingDictationSession? streaming,
        long dictStartTicks)
    {
        if (streaming?.FinishStats is not { } fs) return;
        timing.AsrWaitMs = fs.AsrWaitMs;
        timing.AsrNativeMs = fs.AsrNativeMs;
        timing.BacklogFrames = fs.BacklogFrames;
        timing.BacklogMs = fs.BacklogMs;
        if (fs.NativeCallStats is { } ns)
        {
            timing.NativeCalls = ns.Count;
            timing.NativeTotalMs = ns.TotalMs;
            timing.NativeMaxMs = ns.MaxMs;
            timing.NativeOver250 = ns.CountOver250Ms;
            if (ns.Over250StartTicks is { Count: > 0 })
                timing.StampOver250(ns.Over250StartTicks, ns.Over250Overflow, dictStartTicks);
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
    /// The silence-gate decision masks the start-cue window (StartCueGateMask);
    /// the drop line's voiced/clear are cue-budget-deducted counts and max-RMS is the post-window max.
    /// </summary>
    private float[]? TrimForTranscription(float[] samples, Guid sessionId, out int removedMs)
    {
        // Give the gate the cue window AND the cue's deductible budget
        // (cue-budget deduction, 2026-08-03: in-window frames count, up to the
        // budget of them is deducted -- the old window EXCLUSION dropped prompt
        // short replies). Gated on the player's actual Enabled state (NOT a
        // settings snapshot: PlaySounds is applied to the player once at boot, so
        // the player is the single honest source of whether a cue was emitted) and
        // sized from the pre-roll the recorder ACTUALLY seeded this session (NOT
        // the worst-case request: prewarm-off/drained-ring sessions shrink the
        // window instead of eating post-hotkey speech). Trimming offsets and the
        // transcribed audio are unaffected by the mask by construction.
        var cueMaskMs = StartCueGateMask.ComputeMaskMs(_lastSessionPrerollMs, _sounds.StartCueMs, _sounds.Enabled);
        var cueBudgetMs = StartCueGateMask.ComputeCueBudgetMs(_sounds.StartCueMs, _sounds.Enabled);
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples, cueMaskMs, cueBudgetMs, _lastSessionPrerollMs);
        _lastHeadSpeechAtMs = result.HeadSpeechAtMs;
        _lastHeadClipped = result.HeadClipped;
        removedMs = result.RemovedMs;
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            // voiced/clear/max-RMS make the provisional gate constants
            // recalibratable from logs and a dropped short utterance
            // diagnosable after the fact. Content-free: numbers only.
            // Since 2026-08-03 these are cue-budget-DEDUCTED counts — cue mask
            // and budget are logged alongside so recalibration reads stay honest.
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000}, cue mask {CueMaskMs} ms, cue budget {CueBudgetMs} ms)",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms, cueMaskMs, cueBudgetMs);
            return null;
        }

        if (result.RemovedMs > 0)
            _log.LogInformation(
                "trimmed silence: {Ms} ms across {Runs} runs",
                result.RemovedMs, result.RunsTrimmed);

        return result.Trimmed;
    }

    private static int TotalSince(DateTimeOffset releaseAt)
        => (int)(DateTimeOffset.UtcNow - releaseAt).TotalMilliseconds;

    /// <summary>
    /// Emits the one-line per-dictation timing summary (INF, grep:
    /// "dictation timing") and a [WRN] per stage-budget overrun (grep:
    /// "slow dictation stage"). Complements -- never replaces -- the
    /// existing one-off timing logs (trimmed silence, injection
    /// interrupted, retained parks, ...).
    /// </summary>
    private void EmitTimingSummary(Winpepper.Core.Diagnostics.DictationTimingSummary timing)
    {
        // Window = recording start -> emit. prewarm_active correlates the
        // 07-28 regression suspect (cleanup pre-warm CPU load concurrent
        // with dictation) directly on the line; gc= deltas test the
        // GC-pause/allocation-churn hypothesis. Zero-cost reads.
        timing.GcGen0 = GC.CollectionCount(0) - _gcGen0AtStart;
        timing.GcGen1 = GC.CollectionCount(1) - _gcGen1AtStart;
        timing.GcGen2 = GC.CollectionCount(2) - _gcGen2AtStart;
        // GetTotalPauseDuration: cumulative process-wide GC pause time,
        // monotonic by construction (verified on .NET 9; includes background
        // GC's STW pauses, excludes its concurrent portion).
        timing.GcPauseMs = (int)(GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds;
        timing.PrewarmActive = _cleanupHolder.WasPrewarmActiveSince(_dictStartTicks);
        timing.CpuPegged = _vm.CpuPegged; // same value that drove the pill's pegged meter
        // Head-loss diagnostics (2026-08-04): zero-cost reads of values the
        // pipeline already computed this session.
        timing.PrerollMs = _lastSessionPrerollMs;
        timing.ArmLatencyMs = _lastArmLatencyMs;
        timing.RetriggerGapMs = _retriggerGapMs;
        timing.HeadSpeechAtMs = _lastHeadSpeechAtMs;
        timing.HeadClipped = _lastHeadClipped;
        _log.LogInformation("dictation timing {Summary}", timing.FormatLine());
        foreach (var o in timing.Overruns())
        {
            _log.LogWarning(
                "slow dictation stage {Stage}: {ActualMs} ms (budget {BudgetMs} ms), session {SessionId}",
                o.Stage, o.ActualMs, o.BudgetMs, timing.SessionId);
        }
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
            // tbc0: with listen-start launch, a teardown mid-recording would otherwise
            // leave a running prefetch burst (stop-launch left one only stop→consume).
            // Placed AFTER the bounded run-loop join above so no still-landing start
            // arm can publish a handle after the clear when the join succeeded; a
            // timed-out join means the loop is orphaned with the process exiting
            // (RunLoopJoined=false — same tolerated-leak regime as the cleanup holder).
            _ctxCoordinator?.CancelAndClear();
            _ctxSequencer?.Clear();
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
                // E1 coverage gap: cancel/silence-drop/teardown orphan a
                // wedged pump after ~5 s with DrainTimedOut still false; a
                // gate-holding orphan must still arm the batch routing or the
                // cascade re-enters via cancel.
                _routeGuard.NoteDisposeOutcome(
                    streamingAtTeardown.DrainTimedOut, streamingAtTeardown.PumpCompletion);
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
