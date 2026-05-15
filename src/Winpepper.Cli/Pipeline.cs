#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Core.Sessions;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;
using Winpepper.Platform.WindowContext;

namespace Winpepper.Cli;

public sealed class Pipeline : IDisposable
{
    private readonly ILogger<Pipeline> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly CleanupRunner _cleanup;
    private readonly CorrectionStore _corrections;
    private readonly WindowContextPrefetch _windowContext;
    private readonly SessionEngine _engine = new();

    private IAudioRecorder? _recorder;
    private CancellationTokenSource? _sessionCts;
    private Task<WindowContextResult>? _windowContextTask;

    public Pipeline(
        ILogger<Pipeline> log,
        ILoggerFactory factory,
        string modelDir,
        HotkeyChord hold,
        HotkeyChord toggle,
        HotkeyChord cancel,
        CleanupRunner cleanup,
        CorrectionStore corrections,
        WindowContextPrefetch windowContext)
    {
        _log = log;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
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

                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Start audio capture.
                _recorder = new WasapiRecorder();
                _recorder.Start();

                // Start window-context prefetch in parallel (spec §6.1).
                var hwnd = ForegroundWindow.Handle();
                _log.LogDebug("Hotkey down. Foreground window: '{Title}' ({Hwnd:X})",
                    ForegroundWindow.Title(hwnd), hwnd.ToInt64());
                _windowContextTask = _windowContext.StartAsync(hwnd, _sessionCts.Token);
                break;

            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);

                var samples = _recorder!.Stop();
                _recorder.Dispose();
                _recorder = null;
                _log.LogInformation("Captured {Count} samples ({Sec:F2}s)", samples.Length, samples.Length / 16000.0);

                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _log.LogInformation("Raw transcript: '{Text}'", transcript.Text);

                // Run cleanup (with window context Task piped in).
                var contextTextTask = _windowContextTask is null
                    ? null
                    : _windowContextTask.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.Text : null, ct);

                var cleanupOpts = new CleanupOptions
                {
                    Profile = CleanupProfile.Ordinary,
                    Timeout = TimeSpan.FromSeconds(15),
                    Temperature = 0.1f,
                    WindowContextEnabled = false, // toggle in Plan 3 settings UI
                    WindowContextWait = TimeSpan.FromMilliseconds(500),
                };

                var cleanupResult = await _cleanup.RunAsync(
                    rawTranscript: transcript.Text,
                    corrections: _corrections.Load(),
                    windowContextTask: contextTextTask,
                    options: cleanupOpts,
                    ct: ct);

                _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms, text='{Text}'",
                    cleanupResult.Path, (int)cleanupResult.Elapsed.TotalMilliseconds, cleanupResult.CleanedText);

                _engine.Apply(SessionEvent.TranscriptReady);

                if (!string.IsNullOrWhiteSpace(cleanupResult.CleanedText))
                {
                    var preInjectHwnd = ForegroundWindow.Handle();
                    var preInjectTitle = ForegroundWindow.Title(preInjectHwnd);
                    _log.LogDebug("Injecting into foreground window: '{Title}' ({Hwnd:X})",
                        preInjectTitle, preInjectHwnd.ToInt64());
                    _injector.TryInject(cleanupResult.CleanedText);
                }

                _engine.Apply(SessionEvent.InjectionCompleted);

                _sessionCts?.Dispose();
                _sessionCts = null;
                _windowContextTask = null;
                break;

            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _sessionCts?.Cancel();
                _recorder?.Dispose();
                _recorder = null;
                _windowContextTask = null;
                _sessionCts?.Dispose();
                _sessionCts = null;
                break;

            case HotkeyEventKind.Toggle:
                _log.LogInformation("Toggle hotkey is not implemented in Plan 2 (use hold).");
                break;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
        _sessionCts?.Dispose();
    }
}
#endif
