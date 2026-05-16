using System.ComponentModel;
using System.Diagnostics;
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

    public void Dispose() => _engine.StateChanged -= OnEngineStateChanged;
}
