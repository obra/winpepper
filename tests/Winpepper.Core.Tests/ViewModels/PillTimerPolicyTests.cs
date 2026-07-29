using System;
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class PillTimerPolicyTests
{
    [Theory]
    [InlineData(SessionStage.Idle, false, false)]
    [InlineData(SessionStage.Recording, true, true)]
    [InlineData(SessionStage.Transcribing, true, true)]
    [InlineData(SessionStage.CleaningUp, true, true)]
    [InlineData(SessionStage.Injecting, true, true)]
    // THE fix: PendingPaste persists indefinitely across window switches --
    // the z-order keep-alive must run; the thinking pulse must not.
    [InlineData(SessionStage.PendingPaste, true, false)]
    // Same latent defect, narrower window (6-10 s error holds).
    [InlineData(SessionStage.Error, true, false)]
    public void ForStage_MapsEveryStage(SessionStage stage, bool keepAlive, bool animation)
    {
        var plan = PillTimerPolicy.ForStage(stage);
        plan.KeepAliveRunning.ShouldBe(keepAlive);
        plan.AnimationRunning.ShouldBe(animation);
    }

    [Fact]
    public void AnimationRunning_AgreesWithPillAnimationMap_ForEveryStage()
    {
        // The "no pulse while pending" guarantee must be a pinned agreement
        // between the two mappers, not an emergent accident of two files.
        foreach (var stage in Enum.GetValues<SessionStage>())
        {
            PillTimerPolicy.ForStage(stage).AnimationRunning
                .ShouldBe(PillAnimationMap.ForStage(stage) != PillAnimationMode.None,
                    $"stage {stage}");
        }
    }

    [Fact]
    public void KeepAlive_RunsForEveryOnScreenStage()
    {
        // The pill is on screen for every stage except Idle; the periodic
        // AssertTopmost keep-alive must cover all of them. A newly added
        // stage forces a deliberate decision here.
        foreach (var stage in Enum.GetValues<SessionStage>())
        {
            PillTimerPolicy.ForStage(stage).KeepAliveRunning
                .ShouldBe(stage != SessionStage.Idle, $"stage {stage}");
        }
    }
}
