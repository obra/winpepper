using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyLifecycleGateTests
{
    [Fact]
    public async Task DisposeWaitsForActiveOperationAndRejectsLaterOperations()
    {
        var gate = new HotkeyLifecycleGate(nameof(HotkeyLifecycleGateTests));
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        using var disposeAttempted = new ManualResetEventSlim();
        var cleanedUp = false;

        var operation = Task.Run(() => gate.Run(() =>
        {
            operationEntered.Set();
            releaseOperation.Wait(TestContext.Current.CancellationToken);
            return 42;
        }), TestContext.Current.CancellationToken);
        operationEntered.Wait(TestContext.Current.CancellationToken);

        var dispose = Task.Run(() =>
        {
            disposeAttempted.Set();
            gate.Dispose(() => cleanedUp = true);
        }, TestContext.Current.CancellationToken);
        disposeAttempted.Wait(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        dispose.IsCompleted.ShouldBeFalse();
        cleanedUp.ShouldBeFalse();
        releaseOperation.Set();
        (await operation).ShouldBe(42);
        await dispose;
        cleanedUp.ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => gate.Run(() => 0));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var gate = new HotkeyLifecycleGate(nameof(HotkeyLifecycleGateTests));
        var cleanupCount = 0;

        gate.Dispose(() => cleanupCount++);
        gate.Dispose(() => cleanupCount++);

        cleanupCount.ShouldBe(1);
    }
}
