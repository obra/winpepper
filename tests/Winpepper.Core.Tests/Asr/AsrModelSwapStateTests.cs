using Shouldly;
using Winpepper.Core.Asr;
using Xunit;

namespace Winpepper.Core.Tests.Asr;

public class AsrModelSwapStateTests
{
    [Fact]
    public void Plan_NoSessionLoaded_DesiredPresent_ReturnsLoad()
    {
        var state = new AsrModelSwapState();

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Load);
    }

    [Fact]
    public void Plan_NoSessionLoaded_DesiredMissing_ReturnsCannotStart()
    {
        var state = new AsrModelSwapState();

        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: false)
             .ShouldBe(AsrSwapAction.CannotStart);
    }

    [Fact]
    public void Plan_DoesNotMutateState()
    {
        var state = new AsrModelSwapState();

        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: true);

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
    }
}
