using Shouldly;
using Winpepper.Core.Cleanup;
using Xunit;

namespace Winpepper.Core.Tests.Cleanup;

public class CleanupModelSwapStateTests
{
    [Fact]
    public void Plan_NothingLoaded_DesiredReady_ReturnsLoad()
    {
        var state = new CleanupModelSwapState();

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: true)
             .ShouldBe(CleanupSwapAction.Load);
    }

    [Fact]
    public void Plan_NothingLoaded_DesiredNotReady_ReturnsCannotStart()
    {
        var state = new CleanupModelSwapState();

        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: false)
             .ShouldBe(CleanupSwapAction.CannotStart);
    }

    [Fact]
    public void Plan_DoesNotMutateState()
    {
        var state = new CleanupModelSwapState();

        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: true);

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
    }

    [Fact]
    public void CommitLoad_SetsLoadedNameAndIncrementsGeneration()
    {
        var state = new CleanupModelSwapState();

        state.CommitLoad("qwen2.5-0.5b-instruct-q4_k_m");

        state.LoadedModelName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        state.Generation.ShouldBe(1);
    }

    [Fact]
    public void Plan_SameModelLoaded_ReturnsKeepCurrent()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-a", desiredReady: true)
             .ShouldBe(CleanupSwapAction.KeepCurrent);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredReady_ReturnsSwap()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-b", desiredReady: true)
             .ShouldBe(CleanupSwapAction.Swap);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredNotReady_ReturnsKeepCurrent()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        // Desired model not verified/pre-warmed yet: stay on the working model.
        state.Plan("model-b", desiredReady: false)
             .ShouldBe(CleanupSwapAction.KeepCurrent);
    }

    [Fact]
    public void CommitLoad_AfterSwap_AdvancesLoadedNameAndGeneration()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.CommitLoad("model-b");

        state.LoadedModelName.ShouldBe("model-b");
        state.Generation.ShouldBe(2);
    }

    [Fact]
    public void FailedSwap_NoCommit_KeepsPreviousModelAndGeneration()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        // The holder planned a Swap but adoption failed, so it does NOT call CommitLoad.
        var action = state.Plan("model-b", desiredReady: true);
        action.ShouldBe(CleanupSwapAction.Swap);
        // (no CommitLoad)

        state.LoadedModelName.ShouldBe("model-a");
        state.Generation.ShouldBe(1);
        // The next dictation still wants model-b and will retry the swap.
        state.Plan("model-b", desiredReady: true).ShouldBe(CleanupSwapAction.Swap);
    }
}
