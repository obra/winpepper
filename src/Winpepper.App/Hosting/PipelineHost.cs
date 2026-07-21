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
    private readonly string _modelDir;
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

    private readonly Winpepper.History.HistoryArchiver _archiver;
    private readonly string _asrModelName;
    private readonly string _cleanupModelName;
    private System.Diagnostics.Stopwatch? _recordStopwatch;

    private readonly Winpepper.Core.Errors.ErrorBus _errorBus;
    private Guid _currentSessionId = Guid.Empty;
    private readonly Winpepper.Platform.Injection.ClipboardFallback _clipboardFallback;
    private readonly Winpepper.Core.Notifications.IToastService _toasts;
    private readonly Winpepper.Core.Learning.PostPasteWatcher? _postPaste;
    private readonly Winpepper.Platform.Learning.FocusedElementCapturer? _focusedCapturer;

    public PipelineHost(
        ILoggerFactory factory,
        Winpepper.Core.Errors.ErrorBus errorBus,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir,
        Winpepper.History.HistoryArchiver archiver,
        string asrModelName,
        string cleanupModelName,
        Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
        Winpepper.Core.Notifications.IToastService toasts,
        Winpepper.Cleanup.CleanupRunner? cleanup = null,                       // PLAN2-TYPE
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Cleanup.CleanupOptions? cleanupOptions = null,               // PLAN2-TYPE
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _errorBus = errorBus;
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(
            hold,
            toggle,
            cancel,
            factory.CreateLogger<HotkeyHook>(),
            cancelEnabled: () => _engine.State != SessionState.Idle);
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _modelDir = modelDir;
        _archiver = archiver;
        _asrModelName = asrModelName;
        _cleanupModelName = cleanupModelName;
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
        _cleanupOptions = cleanupOptions ?? new Winpepper.Cleanup.CleanupOptions();
        _clipboardFallback = clipboardFallback;
        _toasts = toasts;
        _postPaste = postPaste;
        _focusedCapturer = focusedCapturer;
    }

    /// <summary>True once the ASR model is loaded and the hotkey pipeline is running.</summary>
    public bool IsRunning { get; private set; }

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

    private bool TryStartCore()
    {
        if (IsRunning) return true;
        if (_asr is null)
        {
            if (!ParakeetSession.ModelFilesPresent(_modelDir))
            {
                _log.LogWarning("ASR model files missing in {ModelDir}; pipeline disabled until models are downloaded", _modelDir);
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                    new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                    Guid.Empty);
                return false;
            }
            try
            {
                _asr = new ParakeetSession(_modelDir);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to load ASR model from {ModelDir}; pipeline disabled", _modelDir);
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
                return false;
            }
        }
        _hotkeyReadiness.Enable(DateTimeOffset.UtcNow);
        IsRunning = true;
        _log.LogInformation("Pipeline started (model dir {ModelDir})", _modelDir);
        return true;
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

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _currentSessionId = Guid.NewGuid();
                _sounds.PlayStart();
                _recorder = new WasapiRecorder();
                _recorder.Start();
                _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();

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
                _recordStopwatch?.Stop();

                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
                _sounds.PlayStop();

                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                var transcript = await Task.Run(() => _asr!.Transcribe(samples), ct);
                transcribeSw.Stop();
                _engine.Apply(SessionEvent.TranscriptReady);

                string final = transcript.Text;
                var cleanupSw = new System.Diagnostics.Stopwatch();
                var cleanupUsedModel = "";
                var windowContextUsed = false;

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

                    var correctionsData = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                    cleanupSw.Start();
                    try
                    {
                        var result = await _cleanup.RunAsync(
                            rawTranscript: final,
                            corrections: correctionsData,
                            windowContextTask: ctxTextTask,
                            options: _cleanupOptions,
                            ct: ct);
                        cleanupSw.Stop();
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                        cleanupUsedModel = _cleanupModelName;
                        windowContextUsed = ctxTextTask is not null
                                            && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                    }
                    catch (Exception ex)
                    {
                        cleanupSw.Stop();
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                    }
                }

                var injectSw = System.Diagnostics.Stopwatch.StartNew();
                var injected = false;
                if (!string.IsNullOrWhiteSpace(final))
                {
                    injected = _injector.TryInject(final);
                    if (!injected)
                    {
                        _errorBus.Report(
                            Winpepper.Core.Errors.ErrorStage.Injection,
                            new InvalidOperationException("SendInput refused; clipboard fallback engaged"),
                            _currentSessionId);
                        _clipboardFallback.Copy(final);
                        _ = _toasts.ShowAsync(
                            "Winpepper",
                            "Couldn't type into the active window. The cleaned text is on your clipboard.",
                            Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                            TimeSpan.FromSeconds(6));
                    }
                }
                injectSw.Stop();
                if (injected && _postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final))
                {
                    var snap = _focusedCapturer.Capture();
                    if (snap.IsValid)
                    {
                        var watchTask = _postPaste.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
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
                    RawTranscript = transcript.Text,
                    CleanedText = final,
                    AsrModelName = _asrModelName,
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
                _recorder?.Dispose(); _recorder = null;
                break;
            case HotkeyEventKind.Toggle:
                if (_engine.State == SessionState.Idle)
                {
                    _engine.Apply(SessionEvent.StartRequested);
                    _currentSessionId = Guid.NewGuid();
                    _sounds.PlayStart();
                    _recorder = new WasapiRecorder();
                    _recorder.Start();
                    _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();

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
                    _recordStopwatch?.Stop();

                    var samples2 = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
                    _sounds.PlayStop();

                    var transcribeSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var transcript2 = await Task.Run(() => _asr!.Transcribe(samples2), ct);
                    transcribeSw2.Stop();
                    _engine.Apply(SessionEvent.TranscriptReady);

                    string final2 = transcript2.Text;
                    var cleanupSw2 = new System.Diagnostics.Stopwatch();
                    var cleanupUsedModel2 = "";
                    var windowContextUsed2 = false;

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

                        var correctionsData2 = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                        cleanupSw2.Start();
                        try
                        {
                            var result2 = await _cleanup.RunAsync(
                                rawTranscript: final2,
                                corrections: correctionsData2,
                                windowContextTask: ctxTextTask2,
                                options: _cleanupOptions,
                                ct: ct);
                            cleanupSw2.Stop();
                            _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                                result2.Path, (int)result2.Elapsed.TotalMilliseconds);
                            final2 = result2.CleanedText;
                            cleanupUsedModel2 = _cleanupModelName;
                            windowContextUsed2 = ctxTextTask2 is not null
                                                && result2.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                        }
                        catch (Exception ex)
                        {
                            cleanupSw2.Stop();
                            _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
                        }
                    }

                    var injectSw2 = System.Diagnostics.Stopwatch.StartNew();
                    var injected2 = false;
                    if (!string.IsNullOrWhiteSpace(final2))
                    {
                        injected2 = _injector.TryInject(final2);
                        if (!injected2)
                        {
                            _errorBus.Report(
                                Winpepper.Core.Errors.ErrorStage.Injection,
                                new InvalidOperationException("SendInput refused; clipboard fallback engaged"),
                                _currentSessionId);
                            _clipboardFallback.Copy(final2);
                            _ = _toasts.ShowAsync(
                                "Winpepper",
                                "Couldn't type into the active window. The cleaned text is on your clipboard.",
                                Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                                TimeSpan.FromSeconds(6));
                        }
                    }
                    injectSw2.Stop();
                    if (injected2 && _postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final2))
                    {
                        var snap = _focusedCapturer.Capture();
                        if (snap.IsValid)
                        {
                            var watchTask = _postPaste.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
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
                        RawTranscript = transcript2.Text,
                        CleanedText = final2,
                        AsrModelName = _asrModelName,
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

    public void Dispose()
    {
        _hotkeyLifecycle.Dispose(() =>
        {
            _hotkeyReadiness.Disable();
            _runCts?.Cancel();
            try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _hook.Dispose();
            _asr?.Dispose();
            _recorder?.Dispose();
        });
    }
}
#endif
