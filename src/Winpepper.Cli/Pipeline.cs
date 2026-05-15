#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Core.Sessions;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;

namespace Winpepper.Cli;

public sealed class Pipeline : IDisposable
{
    private readonly ILogger<Pipeline> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly SessionEngine _engine = new();

    private IAudioRecorder? _recorder;

    public Pipeline(ILogger<Pipeline> log, ILoggerFactory factory, string modelDir,
                    HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel)
    {
        _log = log;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _engine.StateChanged += (from, to) => _log.LogInformation("State {From} -> {To}", from, to);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _hook.Start();
        _log.LogInformation("Winpepper ready. Hold the trigger to dictate.");

        try
        {
            await foreach (var evt in _hook.Events.ReadAllAsync(ct))
            {
                try
                {
                    await HandleHotkey(evt, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Pipeline error in state {State}", _engine.State);
                    _engine.Apply(SessionEvent.Failed);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _recorder = new WasapiRecorder();
                _recorder.Start();
                break;

            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                var samples = _recorder!.Stop();
                _recorder.Dispose();
                _recorder = null;
                _log.LogInformation("Captured {Count} samples ({Sec:F2}s)", samples.Length, samples.Length / 16000.0);
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _log.LogInformation("Transcript: '{Text}'", transcript.Text);
                _engine.Apply(SessionEvent.TranscriptReady);
                if (!string.IsNullOrWhiteSpace(transcript.Text))
                    _injector.TryInject(transcript.Text);
                _engine.Apply(SessionEvent.InjectionCompleted);
                break;

            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _recorder?.Dispose();
                _recorder = null;
                break;

            case HotkeyEventKind.Toggle:
                _log.LogInformation("Toggle hotkey is not implemented in Plan 1 (use hold).");
                break;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
    }
}
#endif
