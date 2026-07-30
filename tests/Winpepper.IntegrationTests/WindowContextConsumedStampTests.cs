using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.IntegrationTests;

public class WindowContextConsumedStampTests
{
    private sealed class EchoBackend : ILlamaCleanupBackend
    {
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
            string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
            => Task.FromResult(rawTranscript);
    }

    // 1a(c): exercises the real path where the per-dictation CTS is created
    // (coordinator.Start), and asserts the timing line's CONSUMED stamp reads
    // ctx_src=uia for a normal dictation. Guards the trap where a cancelled
    // token makes the prefetch quietly return empty: latency looks great,
    // context quality silently dies.
    [Fact]
    public async Task NormalDictation_RealCtsPath_ConsumedStampReadsUia()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) =>
            {
                ct.IsCancellationRequested.ShouldBeFalse(); // the fresh CTS must not be pre-cancelled
                return Task.FromResult(WindowContextResult.FromUia(new string('x', 400)));
            });
        coordinator.OnRecordingStart();
        var handle = coordinator.Start(new IntPtr(42));

        // Same projection PipelineHost uses to adapt the prefetch for the runner.
        var ctxTextTask = handle.Task.ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var runner = new CleanupRunner(new EchoBackend(), NullLogger<CleanupRunner>.Instance);
        var result = await runner.RunAsync(
            rawTranscript: "please clean up this perfectly ordinary transcript",
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: new CleanupOptions { Enabled = true, WindowContextEnabled = true },
            ct: CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(true);
        WindowContextStamp.CtxSrc(result.ConsumedWindowContext, handle.Task).ShouldBe("uia");
    }
}
