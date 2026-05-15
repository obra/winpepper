#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Core.Audio;
using Winpepper.Core.Sessions;
using Winpepper.Core.ViewModels;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;

namespace Winpepper.App.Hosting;

/// <summary>
/// Plan-1 pipeline lifted out of Winpepper.Cli.Pipeline and bound to the
/// session view model + sound-effect player. Cleanup wiring (Plan 2) is added
/// in Task 24 once Plan 2 lands; for Plan 3 we run the raw transcript through
/// injection just like Plan 1 did.
/// </summary>
public sealed class PipelineHost : IDisposable
{
    private readonly ILogger<PipelineHost> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly SessionEngine _engine;
    private readonly SessionViewModel _vm;
    private readonly ISoundEffectPlayer _sounds;
    private IAudioRecorder? _recorder;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public PipelineHost(
        ILoggerFactory factory,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
    }

    public void Start()
    {
        _hook.Start();
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _hook.Events.ReadAllAsync(ct))
            {
                try { await HandleHotkey(evt, ct); }
                catch (Exception ex) { _log.LogError(ex, "pipeline error"); _engine.Apply(SessionEvent.Failed); _vm.NotifyError(ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _sounds.PlayStart();
                _recorder = new WasapiRecorder();
                _recorder.Start();
                break;
            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
                _sounds.PlayStop();
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _engine.Apply(SessionEvent.TranscriptReady);
                if (!string.IsNullOrWhiteSpace(transcript.Text)) _injector.TryInject(transcript.Text);
                _engine.Apply(SessionEvent.InjectionCompleted);
                break;
            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _recorder?.Dispose(); _recorder = null;
                break;
            case HotkeyEventKind.Toggle:
                if (_engine.State == SessionState.Idle)
                {
                    _engine.Apply(SessionEvent.StartRequested);
                    _sounds.PlayStart();
                    _recorder = new WasapiRecorder();
                    _recorder.Start();
                }
                else if (_engine.State == SessionState.Recording)
                {
                    _engine.Apply(SessionEvent.StopRequested);
                    var samples = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
                    _sounds.PlayStop();
                    var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                    _engine.Apply(SessionEvent.TranscriptReady);
                    if (!string.IsNullOrWhiteSpace(transcript.Text)) _injector.TryInject(transcript.Text);
                    _engine.Apply(SessionEvent.InjectionCompleted);
                }
                break;
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
    }
}
#endif
