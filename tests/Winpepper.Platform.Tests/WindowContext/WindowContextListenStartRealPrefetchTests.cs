#if WINDOWS
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Platform.Tests.TestInfra;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

// tbc0 Task 4 — Windows-real prefetch invariant. Drives a REAL WindowContextPrefetch
// (real UiaTreeReader + real OcrFallback, composed exactly like
// WindowContextPrefetch.CreateWindows does) through the real coordinator, sequencer,
// and CleanupRunner (EchoBackend). Excluded on Linux by both the #if WINDOWS guard
// and the [Trait("Platform", "Windows")] (linux-tests.sh runs with -notrait
// "Platform=Windows"); the windows-gate runs it for real on the gate machine.
//
// The read targets a TEST-OWNED window (TestInfra.TestOwnedWindow), not the host's
// ambient foreground: 2026-08-13 + 2026-08-24 gate reds showed the ambient-foreground
// version flakes on whichever window happens to be focused (read cost scales with
// the focused window's UIA tree and its provider responsiveness: 3-node control
// ~= 10-30 ms at 100% cpu; Chrome pages ~= 0.5-3 s; a starved Electron provider
// once stalled a read for 21 s — see artifacts/read-probe/probe*.tsv). The owned
// window is hidden off-screen, never activated, and holds a deterministic
// sentinel-bearing EDIT child, so the read exercises the SAME real machinery in
// tens of ms under any ambient load (guard: TestInfra.TestOwnedWindowTests).
//
// On a degenerate screen state the owned-window read still completes quickly and
// ConsumedWindowContext == true holds; "empty context is a valid real burst for
// timing purposes." Live end-to-end behaviour and ASR contentions (native_max /
// native_over250) remain the owner's dictation-harness readout + the integration
// contention fact — stated plainly.
[Trait("Platform", "Windows")]
public class WindowContextListenStartRealPrefetchTests
{
    private readonly ITestOutputHelper _log;
    public WindowContextListenStartRealPrefetchTests(ITestOutputHelper log) => _log = log;

    private const string RawTranscript = "please clean up this perfectly ordinary transcript";

    private sealed class EchoBackend : ILlamaCleanupBackend
    {
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
            string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
            => Task.FromResult(rawTranscript);
    }

    // Same projection PipelineHost uses (see src/Winpepper.App/Hosting/PipelineHost.cs):
    // Task<WindowContextResult> -> Task<string?> where .Text is on success and null on fault.
    private static Task<string?>? ProjectToTextTask(WindowContextPrefetchHandle? handle)
    {
        if (handle is null) return null;
        return handle.Task.ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static WindowContextPrefetch NewRealPrefetch() =>
        WindowContextPrefetch.CreateWindows(
            new UiaTreeReader(NullLogger<UiaTreeReader>.Instance),
            new OcrFallback(NullLogger<OcrFallback>.Instance),
            NullLogger<WindowContextPrefetch>.Instance);

    private static CleanupRunner NewRunner() =>
        new(new EchoBackend(), NullLogger<CleanupRunner>.Instance);

    private static CleanupOptions TwoSecondBudgetOptions() => new()
    {
        Enabled = true,
        WindowContextEnabled = true,
        WindowContextWait = TimeSpan.FromSeconds(2),
    };

    // The listen-start regime test compares its measured wait against the stop-launch
    // regime's measured wait AND needs the real prefetch duration to schedule its own
    // head-start delay. We compute the stop-launch outcome once and cache it via a
    // Lazy<Task<>> so that whichever test runs first initializes the value and the
    // second reuses the same measurement (LazyThreadSafetyMode.ExecutionAndPublication
    // guarantees a single producer even if xUnit ever runs the two tests concurrently).
    private sealed record StopLaunchOutcome(
        bool Consumed,
        int WaitMs,           // runner-measured WindowContextWaitMs (-1 if null)
        int PrefetchDurationMs, // Stopwatch around the prefetch Task's completion
        int Attempts = 1);    // measurement attempts spent (load-flake retry)

    // 2026-08-13 gate evidence on this host: real-UIA/OCR measurements are
    // load-sensitive — four of five gate runs at 1-min load >= 15 failed on
    // these two facts while the single run at load ~12 passed on identical code.
    // Bounded retry bounds that environment noise without touching any product
    // budget; a genuinely broken prefetch fails EVERY attempt, so the guard stays.
    private const int MaxMeasurementAttempts = 3;

    // The deterministic real-read target, shared by both facts for the process
    // lifetime (windows are destroyed with the test process).
    private static readonly Lazy<TestOwnedWindow?> s_window = new(TestOwnedWindow.Create);

    private static readonly Lazy<Task<StopLaunchOutcome>> s_stopLaunch =
        new(InitializeStopLaunchAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    private static async Task<StopLaunchOutcome> InitializeStopLaunchAsync()
    {
        var outcome = new StopLaunchOutcome(Consumed: false, WaitMs: -1, PrefetchDurationMs: -1, Attempts: 0);
        for (var attempt = 1; attempt <= MaxMeasurementAttempts; attempt++)
        {
            try
            {
                outcome = (await MeasureStopLaunchAsync()) with { Attempts = attempt };
            }
            catch when (attempt < MaxMeasurementAttempts)
            {
                continue; // load-flaked measurement; the final attempt's exception escapes
            }
            if (outcome.Consumed) break;
        }
        return outcome;
    }

    private static async Task<StopLaunchOutcome> MeasureStopLaunchAsync()
    {
        var prefetch = NewRealPrefetch();
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => prefetch.StartAsync(hwnd, ct));

        coordinator.OnRecordingStart();
        var hwnd = s_window.Value!.Hwnd;

        // Stopwatch around the prefetch's OWN completion — independent of the runner's
        // wait. After `await Task.Delay` and the runner call, the prefetch has very
        // likely already completed (consumed=true implies that), but a side
        // WhenAny+ContinueWith pins the exact instant regardless.
        var prefetchSw = Stopwatch.StartNew();
        var handle = coordinator.Start(hwnd);
        _ = Task.WhenAny(handle.Task, Task.Delay(TimeSpan.FromSeconds(10)))
            .ContinueWith(_ => prefetchSw.Stop(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        // "stop": simulate ASR finish 350 ms after the prefetch was launched.
        await Task.Delay(350);

        var ctxTextTask = ProjectToTextTask(handle);
        var runner = NewRunner();
        var result = await runner.RunAsync(
            rawTranscript: RawTranscript,
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: TwoSecondBudgetOptions(),
            ct: CancellationToken.None);

        return new StopLaunchOutcome(
            Consumed: result.ConsumedWindowContext ?? false,
            WaitMs: result.WindowContextWaitMs ?? -1,
            PrefetchDurationMs: (int)prefetchSw.ElapsedMilliseconds);
    }

    private sealed record ListenStartOutcome(bool Consumed, int WaitMs);

    private static async Task<ListenStartOutcome> MeasureListenStartAsync(int prefetchDurationMs)
    {
        var prefetch = NewRealPrefetch();
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => prefetch.StartAsync(hwnd, ct));
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        coordinator.OnRecordingStart();
        var hwnd = s_window.Value!.Hwnd;
        var handle = sequencer.RecordingStarted(startPrefetch: true, hwnd);
        handle.ShouldNotBeNull();

        // utterance + ASR finish + the stop-launch prefetch duration: by this point the
        // listen-start-launched prefetch has had strictly more head-start than it did
        // under the stop-launch regime, so the runner's WhenAny should resolve promptly.
        await Task.Delay(350 + prefetchDurationMs);

        var stoppedHandle = sequencer.RecordingStopped();
        stoppedHandle.ShouldBeSameAs(handle);

        var ctxTextTask = ProjectToTextTask(stoppedHandle);
        var runner = NewRunner();
        var result = await runner.RunAsync(
            rawTranscript: RawTranscript,
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: TwoSecondBudgetOptions(),
            ct: CancellationToken.None);

        result.WindowContextWaitMs.ShouldNotBeNull();
        return new ListenStartOutcome(
            Consumed: result.ConsumedWindowContext ?? false,
            WaitMs: result.WindowContextWaitMs!.Value);
    }

    [Fact]
    public async Task StopLaunchRegime_RealPrefetch_RealUiaOcr_ConsumedTrue()
    {
        if (!OperatingSystem.IsWindows()) return;
        // With no creatable test-owned window the real-prefetch evidence is not
        // observable in this session — skip honestly so the gate log records it.
        Assert.SkipUnless(s_window.Value is not null,
            "could not create the test-owned window in this session — the real-prefetch regime evidence is not observable");

        var outcome = await s_stopLaunch.Value;
        _log.WriteLine(
            $"stop-launch regime (REAL UIA/OCR): consumed={outcome.Consumed}, wait={outcome.WaitMs}ms, prefetch={outcome.PrefetchDurationMs}ms " +
            $"(measurement attempts={outcome.Attempts}/{MaxMeasurementAttempts})");
        outcome.Consumed.ShouldBe(true,
            $"\nExpected ConsumedWindowContext=true but was false on ALL {outcome.Attempts} measurement attempt(s): the real prefetch did not finish within the 2s budget (prefetch={outcome.PrefetchDurationMs}ms, runner_wait={outcome.WaitMs}ms).");
    }

    [Fact]
    public async Task ListenStartRegime_RealPrefetch_RealUiaOcr_ConsumedTrueAndNoLongerWait()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(s_window.Value is not null,
            "could not create the test-owned window in this session — the real-prefetch regime evidence is not observable");

        // Reuse the cached stop-launch outcome to schedule the head-start delay (350ms +
        // the real prefetch duration) and to compare the two measured waits.
        var stopLaunch = await s_stopLaunch.Value;
        // No defensive skip here: the invariants below hold even if stop-launch's
        // prefetch exceeded its 2s budget — the listen-start regime launches strictly
        // earlier so the prefetch has had strictly more time to complete before cleanup.
        var prefetchDurationMs = Math.Max(0, stopLaunch.PrefetchDurationMs);
        var stopLaunchWaitMs = stopLaunch.WaitMs;

        // Same bounded retry as the stop-launch measurement (see MaxMeasurementAttempts):
        // measurement misses under host load are retried; the product budgets and the
        // ordering invariant below are unchanged.
        ListenStartOutcome outcome;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                outcome = await MeasureListenStartAsync(prefetchDurationMs);
            }
            catch (Exception e) when (attempt < MaxMeasurementAttempts)
            {
                _log.WriteLine($"listen-start measurement attempt {attempt}/{MaxMeasurementAttempts} threw {e.GetType().Name}; retrying — {e.Message}");
                continue;
            }
            var pass = outcome.Consumed &&
                       (stopLaunchWaitMs <= 0 || outcome.WaitMs <= stopLaunchWaitMs);
            if (pass || attempt >= MaxMeasurementAttempts) break;
            _log.WriteLine(
                $"listen-start measurement attempt {attempt}/{MaxMeasurementAttempts} missed (consumed={outcome.Consumed}, wait={outcome.WaitMs}ms, stopLaunchWait={stopLaunchWaitMs}ms); retrying");
        }

        outcome.Consumed.ShouldBe(true);
        var listenStartWaitMs = outcome.WaitMs;
        _log.WriteLine(
            $"listen-start regime (REAL UIA/OCR): consumed={outcome.Consumed}, wait={listenStartWaitMs}ms " +
            $"(compare stop-launch regime wait={stopLaunchWaitMs}ms; same real burst, strictly more head-start; " +
            $"real prefetch duration={prefetchDurationMs}ms)");

        // The signal: launching the prefetch at listen-start (NOT at stop) cannot make
        // the runner wait LONGER than it did under the stop-launch regime on the SAME
        // foreground — it has strictly more head-start. The bound is the strongest
        // invariant we can assert against a real burst on a real foreground without a
        // duplicate-burst guarantee (UIA walk + OCR is variable per call); it does
        // prove the ordering is preserved even when the burst is short (degenerate VM).
        // Whole-branch review F2: when the stop-launch regime's runner never waited at
        // all (wait == 0), the real burst beat the simulated finish window on this
        // machine — the regime comparison carries no signal, so log it instead of
        // passing a vacuous comparison.
        if (stopLaunchWaitMs > 0)
        {
            listenStartWaitMs.ShouldBeLessThanOrEqualTo(stopLaunchWaitMs,
                $"\nlisten-start regime wait ({listenStartWaitMs}ms) EXCEEDED the stop-launch regime wait ({stopLaunchWaitMs}ms) " +
                $"on the same foreground; the launch-at-listen-start should strictly increase head-start (prefetch duration was {prefetchDurationMs}ms).");
        }
        else
        {
            _log.WriteLine(
                "stop-launch wait was 0 — the real burst beat the simulated finish window on this machine; " +
                "the wait comparison is not observable here; the consumed-invariants above are the evidence.");
        }
    }
}
#endif
