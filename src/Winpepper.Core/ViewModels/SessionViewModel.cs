using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Winpepper.Core.Errors;
using Winpepper.Core.Pending;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;

namespace Winpepper.Core.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private readonly IUiThread _ui;
    private readonly IDelayScheduler _delays;
    private readonly SessionEngine _engine;
    private readonly ILogger? _log;
    private readonly Stopwatch _stopwatch = new();
    private SessionStage _stage = SessionStage.Idle;
    private string _statusText = "Ready";
    private long _elapsedMs;
    private ErrorStage? _lastErrorStage;
    private string _lastErrorMessage = "";
    private IDisposable? _busSub;
    private readonly Winpepper.Core.Audio.LevelMeterModel _levelMeter = new();
    private double _inputLevel;
    private readonly PendingPasteState _pending = new();
    // Bumped by EVERY change of what the pill is showing. A scheduled clear
    // carries the token it was issued with and no-ops when the token is stale,
    // so a timer can never clobber a newer state (an in-flight dictation, a
    // newer error, a pending paste).
    private int _presentationGeneration;
    // UI-thread mirror of the ENGINE state. EVENT-error scoping must key on
    // this, not _stage: once an error takes the pill _stage reads Error, which
    // would wrongly report "not in flight" while the engine is still Recording.
    private SessionState _engineState = SessionState.Idle;
    // ONE entry per stage with a currently-true CONDITION. A map, not a single
    // slot: two conditions can be true at once and each clears independently
    // on ITS recovery.
    private readonly Dictionary<ErrorStage, string> _activeConditions = new();

    /// <summary>
    /// The _presentationGeneration stamp of the CONDITION that most recently
    /// grabbed the pill, or 0 if none has. NotifyConditionRecovered releases
    /// the pill ONLY when this still equals _presentationGeneration - i.e.
    /// only when a condition is what is actually on screen. Without it, a
    /// recovery would wipe an UNRELATED newer EVENT error off the pill (mic
    /// condition retires to the tray -> an Injection EVENT error takes the
    /// pill mid-dictation -> frames resume -> NotifyConditionRecovered sees
    /// _stage == Error and blows away the injection error the user has not
    /// seen yet, along with its own scheduled self-clear).
    /// </summary>
    private int _conditionPresentationGeneration;

    /// <summary>How long an EVENT error holds the pill before it self-clears.</summary>
    public const int EventErrorHoldMs = 6000;

    /// <summary>
    /// How long a CONDITION grabs the pill before retiring to the tray. The
    /// condition itself is NOT cleared by this timer - only the pill is.
    /// </summary>
    public const int ConditionPillHoldMs = 10000;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null, ILogger? log = null)
    {
        _engine = engine;
        _ui = ui;
        _delays = delays ?? new SystemDelayScheduler();
        _log = log;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public SessionStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            var previous = _stage;
            _stage = value;
            _presentationGeneration++;
            if (value != SessionStage.Recording)
            {
                _levelMeter.Reset();
                InputLevel = 0;
            }
            // Observability (UI latency markers): the closest log proxy for
            // pill visible/hidden. Runs on the UI thread; actual hide adds
            // the fixed 600 ms StatusPillWindow._hideTimer delay downstream.
            // INF because minimumLevel is hard-coded to Information.
            _log?.LogInformation("pill stage {From} -> {To}", previous, value);
            Raise(nameof(Stage));
            Raise(nameof(StatusText));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; Raise(nameof(StatusText)); }
    }

    public long ElapsedMs
    {
        get => _elapsedMs;
        private set { if (_elapsedMs == value) return; _elapsedMs = value; Raise(nameof(ElapsedMs)); }
    }

    // --- CPU-pegged near-start sampling (Feature: pill pegged indicator) ---
    // Sampling rides the pill's existing 100 ms tick -- no new threads/timers.
    // All writes happen on the UI thread (Recording arm via _ui.Post, Tick via
    // the pill's DispatcherTimer); the volatile int lets PipelineHost's run loop
    // read the decision for the timing line without tearing.

    /// <summary>Raw cumulative system times (GetSystemTimes semantics), wired in
    /// AppShell to ProcessResourceSampler.SystemTimes(). Null delegate or null
    /// reading => no decision (CpuPegged stays null, log field omitted).</summary>
    public Func<(long Idle100ns, long Kernel100ns, long User100ns)?>? SystemTimesSampler { get; set; }

    private (long Idle100ns, long Kernel100ns, long User100ns)? _cpuBaseline;
    private int _cpuTicksSinceStart;
    private volatile int _cpuPeggedState; // 0=pending 1=no-reading 2=not-pegged 3=pegged

    /// <summary>Null until decided (or when no reading was possible); otherwise
    /// fixed for the rest of the dictation and shown for the pill's lifetime.</summary>
    public bool? CpuPegged => _cpuPeggedState switch { 2 => false, 3 => true, _ => null };

    /// <summary>
    /// Smoothed microphone level (0..1) while recording, for the pill's voice
    /// meter. Zero when not recording. Fed via <see cref="ReportAudioFrame"/>.
    /// </summary>
    public double InputLevel
    {
        get => _inputLevel;
        private set
        {
            if (Math.Abs(_inputLevel - value) < 0.0001) return;
            _inputLevel = value;
            Raise(nameof(InputLevel));
        }
    }

    public ErrorStage? LastErrorStage
    {
        get => _lastErrorStage;
        private set { if (_lastErrorStage == value) return; _lastErrorStage = value; Raise(nameof(LastErrorStage)); }
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set { if (_lastErrorMessage == value) return; _lastErrorMessage = value; Raise(nameof(LastErrorMessage)); }
    }

    /// <summary>Stage of the most relevant active CONDITION (null when none).
    /// More than one condition can be true at once (e.g. mic unavailable AND a
    /// speech-model load failure); each clears independently on ITS recovery.</summary>
    public ErrorStage? ActiveConditionStage =>
        _activeConditions.Count == 0 ? null : _activeConditions.Keys.Last();

    /// <summary>User-facing text of ALL active conditions ("" when none).</summary>
    public string ActiveConditionMessage =>
        _activeConditions.Count == 0 ? "" : string.Join(" | ", _activeConditions.Values);

    /// <summary>True while any ongoing condition is unresolved (drives the tray).</summary>
    public bool HasActiveCondition => _activeConditions.Count > 0;

    /// <summary>True while a pending paste is held in memory awaiting a pill click.</summary>
    public bool HasPendingPaste => _pending.HasPending;

    /// <summary>The deferred text held in the pending slot (memory only, never persisted).</summary>
    public string PendingPasteText => _pending.PendingText;

    private const string PendingPasteStatus = "Click to paste";
    private const string PendingPasteElevatedStatus = "Admin window - switch & click";

    private static string PendingStatusFor(PendingPasteReason reason)
        => reason == PendingPasteReason.ElevatedTarget ? PendingPasteElevatedStatus : PendingPasteStatus;

    /// <summary>
    /// Enter the pending-paste state: hold the final text in memory (never
    /// persisted) and show the pill's PENDING visual. Because Stage becomes
    /// PendingPaste (not Idle), the pill's Idle auto-hide does not fire.
    /// The reason selects the pill copy: an elevated-target park explains
    /// WHY nothing was typed (UIPI) and what to do next; everything else
    /// keeps the classic "Click to paste".
    /// </summary>
    public void EnterPendingPaste(string text, InjectionTarget target,
        PendingPasteReason reason = PendingPasteReason.Interrupted) => _ui.Post(() =>
    {
        _pending.HoldOrAppend(text, target, reason);
        Stage = SessionStage.PendingPaste;
        StatusText = PendingStatusFor(reason);
    });

    /// <summary>
    /// Update the pill copy for a paste attempt that KEPT the slot (the
    /// pill-click retry path): an elevated block shows the admin-window
    /// copy; any other kept-slot outcome restores the default. No-op when
    /// nothing is pending. Stage stays PendingPaste so the pill remains
    /// clickable -- the slot itself is untouched.
    /// </summary>
    public void ShowPendingPasteStatus(PendingPasteReason reason) => _ui.Post(() =>
    {
        if (!_pending.HasPending) return;
        StatusText = PendingStatusFor(reason);
    });

    /// <summary>
    /// Report the outcome of a pill-click paste attempt (called on the UI
    /// thread by the pill click handler). On success the slot is consumed and
    /// the VM returns to Idle; on failure the slot is kept so the user can
    /// click again. Returns true when the slot was consumed.
    /// </summary>
    public bool NotifyPasteAttempted(bool injected)
    {
        var consumed = _pending.OnPasteAttempted(injected);
        if (consumed)
        {
            Stage = SessionStage.Idle;
            StatusText = "Ready";
        }
        return consumed;
    }

    public void AttachErrorBus(ErrorBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _busSub?.Dispose();
        _busSub = bus.Subscribe(OnBusReport);
    }

    /// <summary>
    /// ERROR TAXONOMY (see <see cref="ErrorClassifier"/>):
    ///
    ///  * EVENT     - a fact about a past moment. No ongoing validity, so it
    ///    only takes the pill while a dictation is in flight and self-clears
    ///    after <see cref="EventErrorHoldMs"/>. At idle it is RECORDED only
    ///    (LastErrorStage/LastErrorMessage feed Diagnostics and the tray text).
    ///  * CONDITION - an ongoing state, so it surfaces even at idle.
    ///
    /// This is the fix for the 2026-07-24 incident: a mid-resume mic fault was
    /// mirrored into Stage=Error while IDLE, and StatusPillWindow's Error arm
    /// stops the auto-hide timer, so the pill squatted on screen for 3.5 hours.
    /// </summary>
    private void OnBusReport(ErrorRecord rec) => _ui.Post(() =>
    {
        LastErrorStage = rec.Stage;
        LastErrorMessage = rec.Message;

        if (ErrorClassifier.Classify(rec) == ErrorKind.Condition)
        {
            EnterCondition(rec.Stage, rec.Message);
            return;
        }

        // While a pending paste is held AT IDLE (e.g. a failed pill-click
        // retry), keep the pill in its clickable PENDING state instead of
        // flipping to Error so the user can click again. The error is still
        // recorded above (Diagnostics/tray); whether it also toasts is
        // AppShell's ErrorToastPolicy, independent of this guard. Scoped to
        // NOT-in-flight since parks survive dictations (council 2026-07-28):
        // an error DURING a dictation started over a held park must still
        // present -- an unconditional return here silently dropped that
        // dictation's failure.
        if (_pending.HasPending && !SessionStages.IsDictationInFlight(_engineState)) return;
        // Idle scoping: an EVENT error has no ongoing validity, so outside a
        // live dictation it never takes the pill. Keyed on the ENGINE state:
        // the presentation stage reads Error while an error is showing and
        // cannot answer "is the user mid-dictation?".
        if (!SessionStages.IsDictationInFlight(_engineState)) return;
        ShowTransientError($"Error ({rec.Stage}): {rec.Message}");
    });

    /// <summary>
    /// Show an EVENT error on the pill and schedule its return to Idle. The
    /// generation token makes the scheduled clear a no-op if anything newer
    /// took the pill in the meantime.
    /// </summary>
    private void ShowTransientError(string text)
    {
        Stage = SessionStage.Error;
        StatusText = text;
        var token = ++_presentationGeneration;
        _delays.Schedule(
            TimeSpan.FromMilliseconds(EventErrorHoldMs),
            () => _ui.Post(() => ReleasePillIfUnchanged(token)));
    }

    /// <summary>
    /// Enter (or refresh) an ongoing CONDITION. A NEW condition grabs the pill
    /// for <see cref="ConditionPillHoldMs"/> as an attention grab, then the
    /// pill retires and the condition lives on the persistent surface (tray)
    /// until a RECOVERY SUCCESS clears it. Retiring the pill does NOT clear
    /// the condition - that is the whole point of the taxonomy.
    /// </summary>
    private void EnterCondition(ErrorStage stage, string message)
    {
        var isRefresh = _activeConditions.ContainsKey(stage);
        _activeConditions[stage] = message;
        Raise(nameof(ActiveConditionStage)); Raise(nameof(ActiveConditionMessage)); Raise(nameof(HasActiveCondition));

        // Re-reports of an ALREADY-SURFACED condition (each failed endpoint-driven
        // rebuild re-raises CaptureFaulted) update the tray text but do NOT
        // re-grab the pill: under device churn an enter-or-refresh grab would
        // keep the pill on screen indefinitely - the original defect, softened.
        if (isRefresh) return;

        // A held pending paste owns the pill; the condition is already on the
        // tray, which is where a long-lived condition belongs anyway.
        if (_pending.HasPending) return;
        Stage = SessionStage.Error;
        StatusText = $"Error ({stage}): {message}";
        var token = ++_presentationGeneration;
        // Stamp the pill as CONDITION-owned so a later recovery can tell
        // "my condition is on screen" from "something newer replaced it".
        _conditionPresentationGeneration = token;
        _delays.Schedule(TimeSpan.FromMilliseconds(ConditionPillHoldMs),
            () => _ui.Post(() => ReleasePillIfUnchanged(token)));
    }

    /// <summary>
    /// A recovery SUCCESS for <paramref name="stage"/> - the ONLY thing that
    /// clears a condition. Called by the host when the warm microphone stream
    /// is proven delivering frames again, or when a speech model actually
    /// loads. Recovery removes only ITS stage's entry: another still-true
    /// condition keeps the surface. Because the entry is removed, a genuine
    /// fault AFTER a recovery is a fresh condition and correctly grabs the
    /// pill again.
    ///
    /// IDEMPOTENT BY CONTRACT (load-bearing): clearing a stage that has no
    /// active condition is a silent no-op, and clearing the SAME stage twice is
    /// harmless. Task 6's recorder relies on both - it re-asserts the recovery
    /// after reporting a fault so a frame that consumed the one-shot recovery
    /// signal before the condition was recorded cannot strand it. Do not make
    /// this method throw, log, or reset anything on the no-entry path.
    /// </summary>
    public void NotifyConditionRecovered(ErrorStage stage) => _ui.Post(() =>
    {
        if (!_activeConditions.Remove(stage)) return;
        Raise(nameof(ActiveConditionStage)); Raise(nameof(ActiveConditionMessage)); Raise(nameof(HasActiveCondition));
        // Release the pill only when NO condition remains - a remaining
        // condition keeps the surface (pill and tray text).
        if (_activeConditions.Count > 0) return;
        if (_pending.HasPending) return;
        if (_stage != SessionStage.Error) return;
        // ...and only when a CONDITION is what is actually on the pill. If a
        // newer EVENT error took it (bumping _presentationGeneration past the
        // condition's stamp), that error owns the pill and has its own
        // self-clear scheduled; clearing it here would hide an unrelated error
        // the user has not seen yet.
        if (_conditionPresentationGeneration != _presentationGeneration) return;
        // RESYNC, never a hard reset - see ReleasePillIfUnchanged for why
        // "Idle / Ready" mid-dictation hides the pill, lies on the tray, and
        // kills the voice meter.
        ResyncPillToEngineState();
    });

    /// <summary>
    /// Release the pill from an error presentation unless something newer owns
    /// it. It RESYNCS to the live engine state - it does NOT hard-reset to
    /// Idle. That distinction is load-bearing: an EVENT error only ever takes
    /// the pill while a dictation is IN FLIGHT, so when its hold expires the
    /// normal case is that we are STILL Recording/Transcribing/Injecting. The
    /// generation token only guards against a state change that happens AFTER
    /// the error took the pill; when the engine was already in flight and did
    /// not transition during the hold, nothing else restores the stage. A hard
    /// "Idle / Ready" there would:
    ///   * hide the pill mid-dictation (StatusPillWindow.xaml.cs:148-155 - the
    ///     Idle arm clears _visible and starts the auto-hide timer),
    ///   * make the tray read "Winpepper - Ready" while recording, and
    ///   * kill the voice meter for the rest of the session (ReportAudioFrame,
    ///     SessionViewModel.cs:170-174, early-returns unless
    ///     _stage == Recording, and the engine never re-raises Recording).
    /// NEVER clears a CONDITION - conditions live on the tray until a recovery
    /// success.
    /// </summary>
    private void ReleasePillIfUnchanged(int token)
    {
        if (token != _presentationGeneration) return; // newer state took the pill
        if (_stage != SessionStage.Error) return;     // stage already moved on
        // NOTE: no HasPending early-return here. Since parks survive dictations
        // (council 2026-07-28) an error CAN own the pill while a park is held;
        // refusing to act would strand Stage=Error forever (the 2026-07-24
        // squatting-pill class). "Click-to-paste wins" is honored by the
        // resync below, whose idle arm restores the PENDING pill.
        ResyncPillToEngineState();
    }

    /// <summary>
    /// Put the pill back in step with the ENGINE - the single source of truth
    /// for "what is this session actually doing right now?". Mirrors the
    /// OnEngineStateChanged switch (minus its stopwatch/pending side effects,
    /// which belong to real transitions); keep the two in step.
    /// </summary>
    private void ResyncPillToEngineState()
    {
        switch (_engineState)
        {
            case SessionState.Recording:
                Stage = SessionStage.Recording;
                StatusText = "Recording...";
                break;
            case SessionState.Transcribing:
                Stage = SessionStage.Transcribing;
                StatusText = "Transcribing...";
                break;
            case SessionState.CleaningUp:
                Stage = SessionStage.CleaningUp;
                StatusText = "Cleaning up...";
                break;
            case SessionState.Injecting:
                Stage = SessionStage.Injecting;
                StatusText = "Inserting...";
                break;
            default:
                // Mirror of the OnEngineStateChanged Idle arm: a held park
                // must get its PENDING pill (reason-correct copy) back, not
                // an auto-hiding "Ready".
                if (_pending.HasPending)
                {
                    Stage = SessionStage.PendingPaste;
                    StatusText = PendingStatusFor(_pending.Reason);
                    break;
                }
                Stage = SessionStage.Idle;
                StatusText = "Ready";
                break;
        }
    }

    /// <summary>
    /// A per-dictation pipeline failure reported directly by the host (not via
    /// the bus). Treated as an EVENT: shown now, self-cleared after
    /// <see cref="EventErrorHoldMs"/> - a pipeline error must not strand the
    /// pill either.
    ///
    /// INTENTIONALLY NOT idle-scoped: the host calls this after
    /// SessionEvent.Failed, when the engine is ALREADY Idle, and the
    /// pre-existing NotifyError_Sets_ErrorStage_With_Message depends on the
    /// error still showing. A future "consistency" edit adding the in-flight
    /// check here is a REGRESSION, not a cleanup.
    ///
    /// INTENTIONALLY NOT pending-scoped either: the single production caller
    /// is a real per-dictation failure, never a background report. Since
    /// parks survive dictations (council 2026-07-28), a HasPending guard
    /// here silently dropped the failure of any dictation started over a
    /// held park. The park is not lost: the self-clear resync restores the
    /// PENDING pill after the error's hold.
    /// </summary>
    public void NotifyError(string message) => _ui.Post(() =>
    {
        ShowTransientError($"Error: {message}");
    });

    public void Tick() => _ui.Post(() =>
    {
        if (_stopwatch.IsRunning) ElapsedMs = _stopwatch.ElapsedMilliseconds;

        if (_cpuPeggedState == 0
            && Stage == SessionStage.Recording
            && ++_cpuTicksSinceStart >= Winpepper.Core.Diagnostics.CpuPeggedPolicy.SampleAfterTicks)
        {
            _cpuPeggedState =
                _cpuBaseline is { } s0
                && SystemTimesSampler?.Invoke() is { } s1
                && Winpepper.Core.Diagnostics.DictationTimingSummary.SystemCpuPercent(
                       s1.Idle100ns - s0.Idle100ns,
                       s1.Kernel100ns - s0.Kernel100ns,
                       s1.User100ns - s0.User100ns) is { } pct
                    ? (Winpepper.Core.Diagnostics.CpuPeggedPolicy.IsPegged(pct) ? 3 : 2)
                    : 1; // evaluated, no reading -- never retried, field omitted from the log
        }
    });

    /// <summary>
    /// Feed a raw mono float frame from the live dictation recorder. Updates
    /// the smoothed <see cref="InputLevel"/> on the UI thread. Frames received
    /// while not recording are ignored so the meter reads zero between sessions.
    /// The live recorder already emits at ~20 Hz (50 ms buffers), which is
    /// within the target throttle — no extra rate limiting is needed here.
    /// </summary>
    public void ReportAudioFrame(ReadOnlyMemory<float> frame) => _ui.Post(() =>
    {
        if (_stage != SessionStage.Recording) return;
        InputLevel = _levelMeter.Push(frame.Span);
    });

    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            // FIRST, before the switch: the UI-thread mirror of the engine
            // state that OnBusReport scopes EVENT errors by. Do NOT read
            // _engine.State directly from OnBusReport instead - that would be
            // a cross-thread read of a plain property AND would lose the
            // IUiThread.Post ordering this mirror inherits.
            _engineState = to;
            switch (to)
            {
                case SessionState.Recording:
                    // A held park deliberately SURVIVES a new dictation
                    // (council 2026-07-28: preserve/append or fail loud --
                    // never silently drop; supersedes Rule 5 of the
                    // 2026-07-21 pending-paste plan, owner-approved). If this
                    // dictation also parks, EnterPendingPaste appends.
                    _stopwatch.Restart();
                    _cpuBaseline = SystemTimesSampler?.Invoke();
                    _cpuTicksSinceStart = 0;
                    _cpuPeggedState = 0;
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
                case SessionState.Transcribing:
                    Stage = SessionStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case SessionState.CleaningUp:
                    // A REAL engine state (not a presentation overlay): entered
                    // only when the cleanup LLM actually runs (Preflight true),
                    // and exited to Injecting when the runner finishes, so
                    // "Inserting..." is reachable again after cleanup.
                    Stage = SessionStage.CleaningUp;
                    StatusText = "Cleaning up...";
                    break;
                case SessionState.Injecting:
                    Stage = SessionStage.Injecting;
                    StatusText = "Inserting...";
                    break;
                case SessionState.Idle:
                    _stopwatch.Stop();
                    // A held park survives dictations: returning to engine
                    // Idle with a held slot must RESTORE the PENDING pill
                    // (stage + reason-correct copy) -- not leave the last
                    // in-flight copy ("Inserting...") on screen and not
                    // auto-hide the pill.
                    if (_pending.HasPending)
                    {
                        Stage = SessionStage.PendingPaste;
                        StatusText = PendingStatusFor(_pending.Reason);
                        break;
                    }
                    Stage = SessionStage.Idle;
                    StatusText = "Ready";
                    break;
            }
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _busSub?.Dispose();
        _engine.StateChanged -= OnEngineStateChanged;
    }
}
