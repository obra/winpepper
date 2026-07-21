using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class PillAnimationMapTests
{
    [Theory]
    [InlineData(SessionStage.Recording,    PillAnimationMode.VoiceLevel)]
    [InlineData(SessionStage.Transcribing, PillAnimationMode.Thinking)]
    [InlineData(SessionStage.CleaningUp,   PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Injecting,    PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Idle,         PillAnimationMode.None)]
    [InlineData(SessionStage.Error,        PillAnimationMode.None)]
    public void ForStage_MapsEachStage(SessionStage stage, PillAnimationMode expected)
    {
        PillAnimationMap.ForStage(stage).ShouldBe(expected);
    }
}
