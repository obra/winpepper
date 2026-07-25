using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorClassifierTests
{
    private static ErrorRecord Record(ErrorStage stage, Exception ex) => new()
    {
        Stage = stage,
        Message = ex.Message,
        ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
        StackTrace = "",
        TimestampUtc = DateTime.UtcNow,
        SessionId = Guid.Empty,
    };

    [Fact]
    public void CaptureFault_Is_A_Condition()
    {
        var rec = Record(ErrorStage.Audio,
            new MicrophoneUnavailableException(new InvalidOperationException("Element not found.")));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Condition);
    }

    [Fact]
    public void MicrophoneUnavailableException_Preserves_Inner_Message()
    {
        var inner = new InvalidOperationException("Element not found.");
        var wrapped = new MicrophoneUnavailableException(inner);

        wrapped.Message.ShouldBe("Element not found.");
        wrapped.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void SilentDictation_Audio_Report_Is_An_Event()
    {
        // WarnIfSessionSilent reports a plain InvalidOperationException at the
        // Audio stage: a fact about the dictation that just ended.
        var rec = Record(ErrorStage.Audio,
            new InvalidOperationException("No audio detected - check your microphone / privacy settings."));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
    }

    [Fact]
    public void MissingSpeechModel_Is_A_Condition()
    {
        var rec = Record(ErrorStage.Asr,
            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Condition);
    }

    [Fact]
    public void AssemblyAi_Config_Rejection_Is_An_Event()
    {
        // AppShell.BuildTranscriber onConfigError reports at Models: the cloud
        // attempt failed but the dictation succeeded via local fallback, and no
        // recovery signal exists that could clear it (governing rule).
        var rec = Record(ErrorStage.Models,
            new InvalidOperationException("AssemblyAI model rejected (foo). Check the model setting."));
        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
    }

    [Theory]
    [InlineData(ErrorStage.Injection)]
    [InlineData(ErrorStage.Cleanup)]
    [InlineData(ErrorStage.OcrUia)]
    [InlineData(ErrorStage.Learning)]
    [InlineData(ErrorStage.History)]
    [InlineData(ErrorStage.Models)]
    [InlineData(ErrorStage.Settings)]
    [InlineData(ErrorStage.Hotkey)]
    [InlineData(ErrorStage.Crash)]
    [InlineData(ErrorStage.Unknown)]
    public void Per_Attempt_Failures_Are_Events(ErrorStage stage)
    {
        var rec = Record(stage, new InvalidOperationException("boom"));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
    }

    [Fact]
    public void Unknown_ExceptionType_At_Audio_Defaults_To_Event()
    {
        // Fail safe: only the explicit condition marker keeps the surface.
        ErrorClassifier.Classify(ErrorStage.Audio, "Some.Other.Exception")
            .ShouldBe(ErrorKind.Event);
    }
}
