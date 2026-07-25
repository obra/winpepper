using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

/// <summary>
/// Pins the shared "is a dictation in flight?" predicate directly. Two
/// consumers depend on it with opposite failure modes: the tray mapper uses it
/// to decide whether an ongoing CONDITION may own the icon, and the view model
/// uses the engine-truth overload to decide whether an EVENT error belongs to a
/// live dictation. A silent change here would move both.
/// </summary>
public class SessionStagesTests
{
    [Theory]
    [InlineData(SessionStage.Recording, true)]
    [InlineData(SessionStage.Transcribing, true)]
    [InlineData(SessionStage.CleaningUp, true)]
    [InlineData(SessionStage.Injecting, true)]
    [InlineData(SessionStage.Idle, false)]
    [InlineData(SessionStage.PendingPaste, false)]
    [InlineData(SessionStage.Error, false)]
    public void Presentation_Stage_Form_Covers_Every_Stage(SessionStage stage, bool expected)
    {
        SessionStages.IsDictationInFlight(stage).ShouldBe(expected);
    }

    /// <summary>The engine has no Error stage, so Error must NOT be treated as
    /// in-flight on the presentation side - that is exactly what lets a
    /// condition keep the tray while it owns the pill.</summary>
    [Fact]
    public void Error_Is_A_Resting_Presentation_Stage()
    {
        SessionStages.IsDictationInFlight(SessionStage.Error).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SessionState.Recording, true)]
    [InlineData(SessionState.Transcribing, true)]
    [InlineData(SessionState.Injecting, true)]
    [InlineData(SessionState.Idle, false)]
    public void Engine_Truth_Form_Covers_Every_State(SessionState state, bool expected)
    {
        SessionStages.IsDictationInFlight(state).ShouldBe(expected);
    }
}
