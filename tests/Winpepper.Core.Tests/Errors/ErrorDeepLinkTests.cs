using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorDeepLinkTests
{
    [Theory]
    [InlineData(ErrorStage.Audio,     "recording")]
    [InlineData(ErrorStage.Asr,       "models")]
    [InlineData(ErrorStage.Cleanup,   "cleanup")]
    [InlineData(ErrorStage.OcrUia,    "cleanup")]
    [InlineData(ErrorStage.Injection, "diagnostics")]
    [InlineData(ErrorStage.Learning,  "corrections")]
    [InlineData(ErrorStage.Models,    "models")]
    [InlineData(ErrorStage.History,   "history")]
    [InlineData(ErrorStage.Settings,  "recording")]
    [InlineData(ErrorStage.Hotkey,    "recording")]
    [InlineData(ErrorStage.Crash,     "diagnostics")]
    [InlineData(ErrorStage.Unknown,   "diagnostics")]
    public void Map_Returns_Nav_Tag_For_Each_Stage(ErrorStage stage, string expected)
    {
        ErrorDeepLink.NavigationTagFor(stage).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ErrorStage.Audio,     "Open Recording settings")]
    [InlineData(ErrorStage.Asr,       "Open Models tab")]
    [InlineData(ErrorStage.Cleanup,   "Open Cleanup settings")]
    [InlineData(ErrorStage.Injection, "Open Diagnostics")]
    public void Action_Label_Reads_For_Humans(ErrorStage stage, string expected)
    {
        ErrorDeepLink.ActionLabelFor(stage).ShouldBe(expected);
    }
}
