using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public sealed class ErrorToastPolicyTests
{
    [Theory]
    // Actionable by the user -> toast.
    [InlineData(ErrorStage.Asr, true)]
    [InlineData(ErrorStage.Models, true)]
    [InlineData(ErrorStage.Settings, true)]
    [InlineData(ErrorStage.Hotkey, true)]
    [InlineData(ErrorStage.Crash, true)]
    // Self-healing / informational -> silent (logs + Diagnostics only).
    [InlineData(ErrorStage.Audio, false)]
    [InlineData(ErrorStage.Injection, false)]
    [InlineData(ErrorStage.Cleanup, false)]
    [InlineData(ErrorStage.OcrUia, false)]
    [InlineData(ErrorStage.Learning, false)]
    [InlineData(ErrorStage.History, false)]
    [InlineData(ErrorStage.Unknown, false)]
    public void ShouldToast_OnlyForActionableStages(ErrorStage stage, bool expected)
        => ErrorToastPolicy.ShouldToast(stage).ShouldBe(expected);
}
