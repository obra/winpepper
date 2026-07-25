using System.ComponentModel;
using System.Diagnostics;
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

    /// <summary>How long an EVENT error holds the pill before it self-clears.</summary>
    public const int EventErrorHoldMs = 6000;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null)
    {
        _engine = engine;
        _ui = ui;
        _delays = delays ?? new SystemDelayScheduler();
        _engine.StateChanged += OnEngineStateChanged;
    }

    public SessionStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
            _presentationGeneration++;
            if (value != SessionStage.Recording)
            {
                _levelMeter.Reset();
                InputLevel = 0;
            }
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

    /// <summary>True while a pending paste is held in memory awaiting a pill click.</summary>
    public bool HasPendingPaste => _pending.HasPending;

    /// <summary>The deferred text held in the pending slot (memory only, never persisted).</summary>
    public string PendingPasteText => _pending.PendingText;

    /// <summary>
    /// Enter the pending-paste state: hold the final text in memory (never
    /// persisted) and show the pill's PENDING visual. Because Stage becomes
    /// PendingPaste (not Idle), the pill's Idle auto-hide does not fire.
    /// </summary>
    public void EnterPendingPaste(string text, InjectionTarget target) => _ui.Post(() =>
    {
        _pending.SetPending(text, target);
        Stage = SessionStage.PendingPaste;
        StatusText = "Click to paste";
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
            // While a pending paste is held, the clickable PENDING pill wins.
            if (_pending.HasPending) return;
            Stage = SessionStage.Error;
            StatusText = $"Error ({rec.Stage}): {rec.Message}";
            return;
        }

        // While a pending paste is held (e.g. a failed pill-click retry), keep
        // the pill in its clickable PENDING state instead of flipping to Error
        // so the user can click again. The error is still recorded above and is
        // surfaced to the user via the toast raised by the caller.
        if (_pending.HasPending) return;
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
        if (_pending.HasPending) return;              // click-to-paste wins
        if (_stage != SessionStage.Error) return;     // stage already moved on
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
            case SessionState.Injecting:
                Stage = SessionStage.Injecting;
                StatusText = "Inserting...";
                break;
            default:
                Stage = SessionStage.Idle;
                StatusText = "Ready";
                break;
        }
    }

    /// <summary>Called by pipeline glue when the cleanup worker starts.</summary>
    public void MarkCleaningUp() => _ui.Post(() =>
    {
        Stage = SessionStage.CleaningUp;
        StatusText = "Cleaning up...";
    });

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
    /// </summary>
    public void NotifyError(string message) => _ui.Post(() =>
    {
        if (_pending.HasPending) return;
        ShowTransientError($"Error: {message}");
    });

    public void Tick() => _ui.Post(() =>
    {
        if (_stopwatch.IsRunning) ElapsedMs = _stopwatch.ElapsedMilliseconds;
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
                    _pending.Discard(); // Rule 5: a new dictation discards any pending paste.
                    _stopwatch.Restart();
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
                case SessionState.Transcribing:
                    Stage = SessionStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case SessionState.Injecting:
                    Stage = SessionStage.Injecting;
                    StatusText = "Inserting...";
                    break;
                case SessionState.Idle:
                    _stopwatch.Stop();
                    // If a pending paste is held, keep the PENDING pill visible
                    // instead of returning to Idle (which would auto-hide it).
                    if (_pending.HasPending) break;
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
