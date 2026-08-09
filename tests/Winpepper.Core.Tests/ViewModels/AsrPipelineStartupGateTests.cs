using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class AsrPipelineStartupGateTests
{
    [Fact]
    public async Task TryStartAsync_DoesNotStartPipelineWhenAsrIsUnverified()
    {
        var verifyCalls = 0;
        var startCalls = 0;
        var notReadyCalls = 0;
        var gate = new AsrPipelineStartupGate(
            _ =>
            {
                verifyCalls++;
                return Task.FromResult(false);
            },
            () =>
            {
                startCalls++;
                return true;
            },
            onNotReady: () => notReadyCalls++);

        var started = await gate.TryStartAsync(TestContext.Current.CancellationToken);

        started.ShouldBeFalse();
        verifyCalls.ShouldBe(1);
        startCalls.ShouldBe(0);
        notReadyCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryStartAsync_StartsPipelineAfterVerifiedAsrReadiness()
    {
        var verifyCalls = 0;
        var startCalls = 0;
        var gate = new AsrPipelineStartupGate(
            _ =>
            {
                verifyCalls++;
                return Task.FromResult(true);
            },
            () =>
            {
                startCalls++;
                return true;
            });

        var started = await gate.TryStartAsync(TestContext.Current.CancellationToken);

        started.ShouldBeTrue();
        verifyCalls.ShouldBe(1);
        startCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryStartAsync_ReturnsFalseWhenVerifiedButPipelineFailsToStart()
    {
        var gate = new AsrPipelineStartupGate(_ => Task.FromResult(true), () => false);

        (await gate.TryStartAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task TryStartAsync_PropagatesVerificationExceptions()
    {
        var gate = new AsrPipelineStartupGate(
            _ => Task.FromException<bool>(new InvalidOperationException("verify blew up")),
            () => true);

        await Should.ThrowAsync<InvalidOperationException>(
            () => gate.TryStartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryStartAsync_RunsPipelineStartOnCapturedSynchronizationContext()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ManualSynchronizationContext();
        var callerThread = Environment.CurrentManagedThreadId;
        var startThread = -1;
        var gate = new AsrPipelineStartupGate(ct => ready.Task.WaitAsync(ct), () =>
        {
            startThread = Environment.CurrentManagedThreadId;
            return true;
        });
        var previous = SynchronizationContext.Current;
        Task<bool>? startup = null;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            startup = gate.TryStartAsync(TestContext.Current.CancellationToken);
            ready.TrySetResult(true);
            context.RunUntil(() => startup.IsCompleted);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        (await startup!).ShouldBeTrue();
        startThread.ShouldBe(callerThread);
    }

    private sealed class ManualSynchronizationContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public void RunUntil(Func<bool> completed)
        {
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (!completed())
            {
                if (_queue.TryDequeue(out var work)) work.Callback(work.State);
                else Thread.Sleep(1);

                if (timeout.Elapsed > TimeSpan.FromSeconds(2))
                    throw new TimeoutException("The startup continuation did not complete.");
            }
        }
    }
}
