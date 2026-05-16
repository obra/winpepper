using System.ComponentModel;
using System.Diagnostics;
using Winpepper.Core.Errors;
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
        private set { if (_stage == value) return; _stage = value; Raise(nameof(Stage)); Raise(nameof(StatusText)); }
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

    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            switch (to)
            {
                case SessionState.Recording:
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
