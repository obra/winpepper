namespace Winpepper.Core.ViewModels;

/// <summary>
/// Which of the status pill's two periodic jobs run in each stage.
/// KeepAlive = the periodic z-order re-assertion (AssertTopmost) and
/// monitor-follow reposition; it must run whenever the pill is on screen,
/// INCLUDING PendingPaste (which persists indefinitely across window
/// switches -- the 2026-07-28 buried-pill fix) and Error. Animation = the
/// 100 ms pulse/meter rendering; it runs only for stages whose
/// PillAnimationMap mode is not None, so PendingPaste shows no thinking
/// pulse (pinned against PillAnimationMap by PillTimerPolicyTests).
/// </summary>
public readonly record struct PillTimerPlan(bool KeepAliveRunning, bool AnimationRunning);

public static class PillTimerPolicy
{
    public static PillTimerPlan ForStage(SessionStage stage) => stage switch
    {
        SessionStage.Idle => new(KeepAliveRunning: false, AnimationRunning: false),
        SessionStage.Recording => new(true, true),
        SessionStage.Transcribing => new(true, true),
        SessionStage.CleaningUp => new(true, true),
        SessionStage.Injecting => new(true, true),
        SessionStage.PendingPaste => new(true, false),
        SessionStage.Error => new(true, false),
        // Unknown/new stage: safe default -- stay on top, no animation. The
        // invariant tests force a deliberate mapping when a stage is added.
        _ => new(true, false),
    };
}
