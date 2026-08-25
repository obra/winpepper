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

    [Fact]
    public void CommitLoad_SetsLoadedNameAndIncrementsGeneration()
    {
        var state = new AsrModelSwapState();

        state.CommitLoad("parakeet-tdt-0.6b-v3");

        state.LoadedModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        state.Generation.ShouldBe(1);
    }

    [Fact]
    public void MarkUnloaded_ClearsLoadedName_KeepsGeneration()
    {
        // 2026-08-25 "None" backup selection: when the user deselects the
        // backup model mid-session (or boots with None), PipelineHost disposes
        // the loaded session itself; the swap state must then report
        // "nothing loaded" so a LATER re-selection plans Load (not KeepCurrent
        // against a session that no longer exists).
        var state = new AsrModelSwapState();
        state.CommitLoad("parakeet-tdt-0.6b-v3");

        state.MarkUnloaded();

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(1);
    }

    [Fact]
    public void Plan_AfterMarkUnloaded_DesiredPresent_ReturnsLoad_NotKeepCurrent()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("parakeet-tdt-0.6b-v3");
        state.MarkUnloaded();

        state.Plan("parakeet-tdt-0.6b-v2", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Load);
        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Load); // same name, but the session is gone
    }

    [Fact]
    public void Plan_SameModelLoaded_ReturnsKeepCurrent()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-a", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.KeepCurrent);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredPresent_ReturnsSwap()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-b", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Swap);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredMissing_ReturnsKeepCurrent()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        // Desired model not downloaded yet: stay on the working model.
        state.Plan("model-b", desiredFilesPresent: false)
             .ShouldBe(AsrSwapAction.KeepCurrent);
    }

    [Fact]
    public void CommitLoad_AfterSwap_AdvancesLoadedNameAndGeneration()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.CommitLoad("model-b");

        state.LoadedModelName.ShouldBe("model-b");
        state.Generation.ShouldBe(2);
    }

    [Fact]
    public void FailedSwap_NoCommit_KeepsPreviousModelAndGeneration()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        // Host planned a Swap but the load threw, so it does NOT call CommitLoad.
        var action = state.Plan("model-b", desiredFilesPresent: true);
        action.ShouldBe(AsrSwapAction.Swap);
        // (no CommitLoad)

        state.LoadedModelName.ShouldBe("model-a");
        state.Generation.ShouldBe(1);
        // Next dictation still wants model-b and will retry the swap.
        state.Plan("model-b", desiredFilesPresent: true).ShouldBe(AsrSwapAction.Swap);
    }
}
