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
/// Plan-3 pipeline host. Wires audio capture → ASR → cleanup (Plan 2) → injection.
/// Cleanup, corrections, and window-context are optional — if absent the raw
/// transcript is injected unchanged (Plan-1 behaviour).
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

    private readonly Winpepper.Cleanup.CleanupRunner? _cleanup;        // PLAN2-TYPE
    private readonly Winpepper.Cleanup.CleanupOptions _cleanupOptions; // PLAN2-TYPE
    private readonly Winpepper.Corrections.CorrectionStore? _corrections; // PLAN2-TYPE
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetch? _windowContext; // PLAN2-TYPE
    private Task<Winpepper.Platform.WindowContext.WindowContextResult>? _ctxPrefetchTask;    // PLAN2-TYPE

    public PipelineHost(
        ILoggerFactory factory,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir,
        Winpepper.Cleanup.CleanupRunner? cleanup = null,                       // PLAN2-TYPE
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Cleanup.CleanupOptions? cleanupOptions = null)               // PLAN2-TYPE
    {
        _log = factory.CreateLogger<PipelineHost>();
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
        _cleanupOptions = cleanupOptions ?? new Winpepper.Cleanup.CleanupOptions();
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
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
                _sounds.PlayStop();
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _engine.Apply(SessionEvent.TranscriptReady);

                string final = transcript.Text;
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

                    var corrections = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                    try
                    {
                        var result = await _cleanup.RunAsync(
                            rawTranscript: final,
                            corrections: corrections,
                            windowContextTask: ctxTextTask,
                            options: _cleanupOptions,
                            ct: ct);
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                    }
                }

                if (!string.IsNullOrWhiteSpace(final)) _injector.TryInject(final);
                _engine.Apply(SessionEvent.InjectionCompleted);
                _ctxPrefetchTask = null;
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
                    var samples2 = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
                    _sounds.PlayStop();
                    var transcript2 = await Task.Run(() => _asr.Transcribe(samples2), ct);
                    _engine.Apply(SessionEvent.TranscriptReady);

                    string final2 = transcript2.Text;
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

                        var corrections2 = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                        try
                        {
                            var result2 = await _cleanup.RunAsync(
                                rawTranscript: final2,
                                corrections: corrections2,
                                windowContextTask: ctxTextTask2,
                                options: _cleanupOptions,
                                ct: ct);
                            _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                                result2.Path, (int)result2.Elapsed.TotalMilliseconds);
                            final2 = result2.CleanedText;
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(final2)) _injector.TryInject(final2);
                    _engine.Apply(SessionEvent.InjectionCompleted);
                    _ctxPrefetchTask = null;
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
