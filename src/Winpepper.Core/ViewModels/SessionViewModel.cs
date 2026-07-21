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

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui)
    {
        _engine = engine;
        _ui = ui;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public SessionStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
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

    private void OnBusReport(ErrorRecord rec) => _ui.Post(() =>
    {
        LastErrorStage = rec.Stage;
        LastErrorMessage = rec.Message;
        // While a pending paste is held (e.g. a failed pill-click retry), keep
        // the pill in its clickable PENDING state instead of flipping to Error
        // so the user can click again. The error is still recorded above and is
        // surfaced to the user via the toast raised by the caller.
        if (_pending.HasPending) return;
        Stage = SessionStage.Error;
        StatusText = $"Error ({rec.Stage}): {rec.Message}";
    });

    /// <summary>Called by pipeline glue when the cleanup worker starts.</summary>
    public void MarkCleaningUp() => _ui.Post(() =>
    {
        Stage = SessionStage.CleaningUp;
        StatusText = "Cleaning up...";
    });

    public void NotifyError(string message) => _ui.Post(() =>
    {
        Stage = SessionStage.Error;
        StatusText = $"Error: {message}";
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
