using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiErrorsTests
{
    [Theory]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"invalid speech_model 'universal-9000'\"}", true)]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"unsupported model\"}", true)]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"bad audio_url\"}", false)]
    public void IsInvalidModel_On400_MatchesModelWording(string message, bool expected)
        => AssemblyAiErrors.IsInvalidModel(new AssemblyAiException(message, 400)).ShouldBe(expected);

    [Fact]
    public void IsInvalidModel_False_WhenNot400()
        => AssemblyAiErrors.IsInvalidModel(new AssemblyAiException("server error", 500)).ShouldBeFalse();
}
