using Shouldly;
using Winpepper.Core.Asr;
using Xunit;

namespace Winpepper.Core.Tests.Asr;

public class OrphanedPumpGuardTests
{
    // Bounded poll (pattern: DebouncedSettingsWriterTests). Deferred disposes run
    // on a threadpool continuation, so give them a generous CI-safe deadline;
    // legitimate failures still trip.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        condition().ShouldBeTrue();
    }

    [Fact]
    public void RunOrDefer_NoTrackedPumps_RunsInline()
    {
        var guard = new OrphanedPumpGuard();
        var ran = false;

        guard.RunOrDefer(() => ran = true);

        ran.ShouldBeTrue(); // synchronous, before RunOrDefer returns
    }

    [Fact]
    public void Register_CompletedPump_IsIgnored()
    {
        var guard = new OrphanedPumpGuard();

        guard.Register(Task.CompletedTask);

        guard.LivePumpCount.ShouldBe(0);
        var ran = false;
        guard.RunOrDefer(() => ran = true);
        ran.ShouldBeTrue(); // still inline
    }

    [Fact]
    public async Task RunOrDefer_LivePump_DoesNotRunUntilPumpCompletes()
    {
        var guard = new OrphanedPumpGuard();
        var pump = new TaskCompletionSource();
        guard.Register(pump.Task);
        var ran = false;

        guard.RunOrDefer(() => ran = true);

        ran.ShouldBeFalse(); // deferred while the pump is live
        pump.SetResult();
        await WaitUntilAsync(() => ran);
    }

    [Fact]
    public async Task RunOrDefer_MultipleLivePumps_WaitsForAll()
    {
        var guard = new OrphanedPumpGuard();
        var pumpA = new TaskCompletionSource();
        var pumpB = new TaskCompletionSource();
        guard.Register(pumpA.Task);
        guard.Register(pumpB.Task);
        var disposeCount = 0;

        guard.RunOrDefer(() => Interlocked.Increment(ref disposeCount));

        pumpA.SetResult();
        await Task.Delay(50);
        Volatile.Read(ref disposeCount).ShouldBe(0); // still gated on pumpB

        pumpB.SetResult();
        await WaitUntilAsync(() => Volatile.Read(ref disposeCount) == 1);
        await Task.Delay(50);
        Volatile.Read(ref disposeCount).ShouldBe(1); // dispose fires exactly once
    }

    [Fact]
    public async Task FaultedPump_StillReleasesTheDeferredDispose()
    {
        var guard = new OrphanedPumpGuard();
        var pump = new TaskCompletionSource();
        guard.Register(pump.Task);
        var ran = false;

        guard.RunOrDefer(() => ran = true);
        pump.SetException(new InvalidOperationException("pump died"));

        await WaitUntilAsync(() => ran);
        guard.LivePumpCount.ShouldBe(0);
    }

    [Fact]
    public async Task PumpRegisteredAfterDefer_DoesNotGateTheEarlierDispose()
    {
        var guard = new OrphanedPumpGuard();
        var earlier = new TaskCompletionSource();
        guard.Register(earlier.Task);
        var ran = false;
        guard.RunOrDefer(() => ran = true);

        var later = new TaskCompletionSource(); // captured the NEW session
        guard.Register(later.Task);
        earlier.SetResult();

        await WaitUntilAsync(() => ran); // never had to wait for `later`
        later.SetResult();
    }

    [Fact]
    public async Task DeferredDisposeThrow_IsRoutedToErrorCallback_NotUnobserved()
    {
        Exception? routed = null;
        var guard = new OrphanedPumpGuard(ex => routed = ex);
        var pump = new TaskCompletionSource();
        guard.Register(pump.Task);

        guard.RunOrDefer(() => throw new InvalidOperationException("dispose boom"));
        pump.SetResult();

        await WaitUntilAsync(() => routed is not null);
        routed.ShouldBeOfType<InvalidOperationException>();
        routed!.Message.ShouldBe("dispose boom");

        // The guard remains usable after a deferred-dispose throw.
        var ran = false;
        guard.RunOrDefer(() => ran = true);
        ran.ShouldBeTrue();
    }

    [Fact]
    public void Prune_KeepsLivePumpCountBounded()
    {
        var guard = new OrphanedPumpGuard();
        for (var i = 0; i < 10; i++)
        {
            var completed = new TaskCompletionSource();
            completed.SetResult();
            guard.Register(completed.Task);
        }
        var live = new TaskCompletionSource();
        guard.Register(live.Task);

        guard.LivePumpCount.ShouldBe(1); // completed pumps were pruned
        live.SetResult();
        guard.LivePumpCount.ShouldBe(0);
    }
}
