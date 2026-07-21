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
        var gate = new AsrPipelineStartupGate(provisioner, () =>
        {
            startCalls++;
            return true;
        });

        var started = await gate.TryStartAsync(TestContext.Current.CancellationToken);

        started.ShouldBeFalse();
        provisioner.VerifyCalls.ShouldBe(1);
        startCalls.ShouldBe(0);
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
}
