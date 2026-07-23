namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure mapping from the session stage to how the status pill animates.
/// Recording → live voice meter; the post-release working stages
/// (Transcribing/CleaningUp/Injecting) → a gentle "thinking" pulse so the user
/// can tell the app is still working; Idle/Error → no animation (Error keeps
/// its steady colour).
/// </summary>
public static class PillAnimationMap
{
    public static PillAnimationMode ForStage(SessionStage stage) => stage switch
    {
        SessionStage.Recording    => PillAnimationMode.VoiceLevel,
        SessionStage.Transcribing => PillAnimationMode.Thinking,
        SessionStage.CleaningUp   => PillAnimationMode.Thinking,
        SessionStage.Injecting    => PillAnimationMode.Thinking,
        SessionStage.PendingPaste => PillAnimationMode.None, // steady; no pulse while waiting for click
        _                         => PillAnimationMode.None, // Idle, Error
    };
}
