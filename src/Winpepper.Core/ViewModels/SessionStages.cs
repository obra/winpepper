using Winpepper.Core.Sessions;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure "is a dictation in flight?" predicates - one shared definition with two
/// truthful inputs. The VM scopes EVENT errors by the ENGINE state (the pill's
/// own stage becomes Error the moment an error shows, so it cannot answer the
/// question); the tray mapper asks about the presentation stage it consumes.
/// </summary>
public static class SessionStages
{
    /// <summary>Presentation-stage form, for the tray mapper: Idle, Error and
    /// PendingPaste are resting/waiting states, not a dictation.</summary>
    public static bool IsDictationInFlight(SessionStage stage) => stage is
        SessionStage.Recording or
        SessionStage.Transcribing or
        SessionStage.CleaningUp or
        SessionStage.Injecting;

    /// <summary>Engine-truth form, for the view model's EVENT-error scoping.
    /// The engine has no Error stage, so it is a faithful in-flight signal
    /// even while an error owns the pill.</summary>
    public static bool IsDictationInFlight(SessionState state) =>
        state is not SessionState.Idle;
}
