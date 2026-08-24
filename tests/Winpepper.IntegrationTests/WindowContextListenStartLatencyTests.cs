using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.IntegrationTests;

// tbc0 Task 4: regime-level MEASURED wait evidence. Drives the REAL production
// objects (WindowContextListenStartSequencer + WindowContextPrefetchCoordinator
// + CleanupRunner) under the two launch-time regimes, asserting numeric bounds
// on the runner-measured WindowContextWaitMs. Lower bounds carry the signal;
// upper bounds are generous for scheduler tolerance.
//
// Anti-vacuity: every assertion is a numeric bound on result.WindowContextWaitMs
// (a Task-2 field that does not exist without Task 2); the files do not compile
// without Task 3's WindowContextListenStartSequencer; a null measurement falls
// outside EVERY bound here, so the tests cannot pass vacuously.
//
// Delay-shaped context tasks stand in for the UIA/OCR burst (real OCR cannot
// run on Linux). The gate machine runs the Windows-real sibling test class
// (WindowContextListenStartRealPrefetchTests) for the real-burst invariant.
// Live end-to-end + contention is the owner's timing-line readout — no
// dictation harness exists.
public class WindowContextListenStartLatencyTests
{
    private readonly ITestOutputHelper _log;
    public WindowContextListenStartLatencyTests(ITestOutputHelper log) => _log = log;

    // rawTranscript >= 4 words so Preflight's BypassShort does not fire before the
    // window-context wait; the EchoBackend returns the raw transcript verbatim, so
    // TranscriptSimilarity retention is 1.0 and the plausibility gates stay quiet.
    private const string RawTranscript = "please clean up this perfectly ordinary transcript";

    private sealed class EchoBackend : ILlamaCleanupBackend
    {
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
            string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
            => Task.FromResult(rawTranscript);
    }

    // Same projection PipelineHost uses to adapt the prefetch for the runner:
    // Task<WindowContextResult> -> Task<string?> (.Text on success, null otherwise).
    private static Task<string?>? ProjectToTextTask(WindowContextPrefetchHandle? handle)
    {
        if (handle is null) return null;
        return handle.Task.ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static CleanupRunner NewRunner() =>
        new(new EchoBackend(), NullLogger<CleanupRunner>.Instance);

    // 1) stop-launch regime (TODAY's behaviour): no launch at "listen-start",
    //    launch the context task AT stop, ASR finishes 350 ms later, runner waits
    //    the ~1150 ms remainder. measured WindowContextWaitMs in [250, 1500].
    //    (2026-08-24: prefetch task widened 700->1500 ms — with 700 ms the
    //    [250, 1500] window broke whenever the middle Task.Delay(350) overshot
    //    by >=450 ms at the gate host's sustained 100% cpu; 1500 ms tolerates
    //    ~900 ms of slippage while keeping every asserted bound identical.
    //    See test #3's comment for the observed flip.)
    [Fact]
    public async Task StopLaunchRegime_PrefetchOutlivesAsrFinish_CleanupWaitsTheRemainder()
    {
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
            Task.Run(async () =>
            {
                await Task.Delay(1500, ct);
                return WindowContextResult.FromUia(new string('x', 400));
            }, ct));

        // "listen-start": no launch in this regime.
        coordinator.OnRecordingStart();

        // "stop": launch the 1500 ms context task via the coordinator, then let
        // streaming finish (350 ms). At cleanup start the prefetch has ~1150 ms
        // of work remaining.
        var handle = coordinator.Start(new IntPtr(42));
        await Task.Delay(350);

        var ctxTextTask = ProjectToTextTask(handle);
        var runner = NewRunner();
        var result = await runner.RunAsync(
            rawTranscript: RawTranscript,
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: new CleanupOptions
            {
                Enabled = true,
                WindowContextEnabled = true,
                WindowContextWait = TimeSpan.FromSeconds(2),
            },
            ct: CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(true);
        result.WindowContextWaitMs.ShouldNotBeNull();
        _log.WriteLine($"stop-launch regime: WindowContextWaitMs={result.WindowContextWaitMs} consumed={result.ConsumedWindowContext}");
        result.WindowContextWaitMs!.Value.ShouldBeInRange(250, 1500);
    }

    // 2) listen-start regime (NEW behaviour), THROUGH the sequencer: a 700 ms
    //    prefetch task is launched at RecordingStarted, the utterance + finish
    //    take 1850 ms (far longer than the prefetch), RecordingStopped hands
    //    the (long-complete) handle back, runner's WhenAny resolves immediately.
    //    measured WindowContextWaitMs < 250 — the signal that the prefetch was
    //    already done before cleanup even asked.
    [Fact]
    public async Task ListenStartRegime_PrefetchReadyAtCleanupStart_CleanupWaitsNothing()
    {
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
            Task.Run(async () =>
            {
                await Task.Delay(700, ct);
                return WindowContextResult.FromUia(new string('x', 400));
            }, ct));
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        // "listen-start": OnRecordingStart THEN the sequencer launches (same
        // order as PipelineHost's start arm). The 700 ms prefetch begins now.
        coordinator.OnRecordingStart();
        var handle = sequencer.RecordingStarted(startPrefetch: true, new IntPtr(42));
        handle.ShouldNotBeNull();

        // utterance + streaming finish: 1850 ms — the 700 ms prefetch has been
        // complete for ~1150 ms by the time cleanup starts.
        await Task.Delay(1850);

        var stoppedHandle = sequencer.RecordingStopped();
        stoppedHandle.ShouldBeSameAs(handle);

        var ctxTextTask = ProjectToTextTask(stoppedHandle);
        var runner = NewRunner();
        var result = await runner.RunAsync(
            rawTranscript: RawTranscript,
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: new CleanupOptions
            {
                Enabled = true,
                WindowContextEnabled = true,
                WindowContextWait = TimeSpan.FromSeconds(2),
            },
            ct: CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(true);
        result.WindowContextWaitMs.ShouldNotBeNull();
        _log.WriteLine($"listen-start regime: WindowContextWaitMs={result.WindowContextWaitMs} consumed={result.ConsumedWindowContext}");
        result.WindowContextWaitMs!.Value.ShouldBeLessThan(250);
    }

    // 3) stop-launch + fast finish + tight budget: 4000 ms context task launched
    //    at "stop", 350 ms streaming finish (≈3650 ms of prefetch work remains),
    //    WindowContextWait budget = 200 ms — the budget EXPIRES before the
    //    remainder (200 < 3650), so the runner drops the context. consumed == false;
    //    measured WindowContextWaitMs in [150, 1200] (the budget bound, plus
    //    scheduler tolerance). (A 400 ms budget would CONSUME here — 400 > 350-ish —
    //    verified arithmetic; 200 ms is what actually exercises the drop branch.)
    //
    //    2026-08-24: the margin was widened from 700->4000 ms. With a 700 ms task
    //    the intended "~350 ms remains at cleanup start" depended on the middle
    //    Task.Delay(350) waking on schedule; under the gate host's sustained
    //    100% cpu that sleep overshot by >=150 ms, the remainder shrank below the
    //    200 ms budget, and the fact flopped to consumed==true (observed gate
    //    red). A 4000 ms task tolerates ~3.6 s of scheduler slippage — far beyond
    //    anything a multi-second test leg can produce — and costs no wall time:
    //    the runner's 200 ms budget still bounds this fact, the abandoned tail
    //    task simply completes in the process background afterwards.
    [Fact]
    public async Task StopLaunchRegime_FastFinish_DropsContextAfterBudget()
    {
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
            Task.Run(async () =>
            {
                await Task.Delay(4000, ct);
                return WindowContextResult.FromUia(new string('x', 400));
            }, ct));

        coordinator.OnRecordingStart();

        // "stop": launch the 4000 ms context task, let streaming finish 350 ms
        // later; ≈3650 ms of prefetch work remains at cleanup start.
        var handle = coordinator.Start(new IntPtr(42));
        await Task.Delay(350);

        var ctxTextTask = ProjectToTextTask(handle);
        var runner = NewRunner();
        var result = await runner.RunAsync(
            rawTranscript: RawTranscript,
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: new CleanupOptions
            {
                Enabled = true,
                WindowContextEnabled = true,
                WindowContextWait = TimeSpan.FromMilliseconds(200),
            },
            ct: CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(false);
        result.WindowContextWaitMs.ShouldNotBeNull();
        _log.WriteLine($"stop-launch-drop regime: WindowContextWaitMs={result.WindowContextWaitMs} consumed={result.ConsumedWindowContext}");
        result.WindowContextWaitMs!.Value.ShouldBeInRange(150, 1200);
    }
}
