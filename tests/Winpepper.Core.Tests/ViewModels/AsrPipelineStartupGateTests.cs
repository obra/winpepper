using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class AsrPipelineStartupGateTests
{
    [Fact]
    public async Task TryStartAsync_DoesNotStartPipelineWhenAsrIsUnverified()
    {
        var provisioner = new FakeProvisioner(ready: false);
        var startCalls = 0;
        var notReadyCalls = 0;
        var gate = new AsrPipelineStartupGate(provisioner, () =>
        {
            startCalls++;
            return true;
        }, onNotReady: () => notReadyCalls++);

        var started = await gate.TryStartAsync(TestContext.Current.CancellationToken);

        started.ShouldBeFalse();
        provisioner.VerifyCalls.ShouldBe(1);
        startCalls.ShouldBe(0);
        notReadyCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryStartAsync_StartsPipelineAfterVerifiedAsrReadiness()
    {
        var provisioner = new FakeProvisioner(ready: true);
        var startCalls = 0;
        var gate = new AsrPipelineStartupGate(provisioner, () =>
        {
            startCalls++;
            return true;
        });

        var started = await gate.TryStartAsync(TestContext.Current.CancellationToken);

        started.ShouldBeTrue();
        provisioner.VerifyCalls.ShouldBe(1);
        startCalls.ShouldBe(1);
    }

    [Fact]
    public async Task TryStartAsync_RunsPipelineStartOnCapturedSynchronizationContext()
    {
        var provisioner = new DeferredProvisioner();
        var context = new ManualSynchronizationContext();
        var callerThread = Environment.CurrentManagedThreadId;
        var startThread = -1;
        var gate = new AsrPipelineStartupGate(provisioner, () =>
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
            provisioner.Complete(ready: true);
            context.RunUntil(() => startup.IsCompleted);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        (await startup!).ShouldBeTrue();
        startThread.ShouldBe(callerThread);
    }

    private sealed class FakeProvisioner(bool ready) : IAsrProvisioningService
    {
        public AsrProvisioningState State { get; } = new(AsrProvisioningStatus.Missing);
        public int VerifyCalls { get; private set; }
        public event EventHandler<AsrProvisioningState>? StateChanged
        {
            add { }
            remove { }
        }
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> VerifyReadyAsync(CancellationToken ct)
        {
            VerifyCalls++;
            return Task.FromResult(ready);
        }
    }

    private sealed class DeferredProvisioner : IAsrProvisioningService
    {
        private readonly TaskCompletionSource<bool> _ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AsrProvisioningState State { get; } = new(AsrProvisioningStatus.Missing);
        public event EventHandler<AsrProvisioningState>? StateChanged
        {
            add { }
            remove { }
        }
        public Task EnsureReadyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<bool> VerifyReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);
        public void Complete(bool ready) => _ready.TrySetResult(ready);
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
