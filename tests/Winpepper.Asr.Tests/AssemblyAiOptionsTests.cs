using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiOptionsTests
{
    [Fact]
    public void Defaults_MatchSingleOwnedBudget()
    {
        var o = new AssemblyAiOptions();
        o.CloudDeadline.ShouldBe(TimeSpan.FromSeconds(10));
        o.PerRequestTimeout.ShouldBe(TimeSpan.FromSeconds(8));
        o.FirstPollDelay.ShouldBe(TimeSpan.FromMilliseconds(750));
        o.PollInterval.ShouldBe(TimeSpan.FromSeconds(1));
        o.DeleteAfterTranscribe.ShouldBeTrue();
        o.KeytermsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, 5)]     // below floor -> 5
    [InlineData(3, 5)]     // below floor -> 5
    [InlineData(45, 30)]   // above ceiling -> 30
    [InlineData(-7, 5)]    // negative -> 5
    public void ClampDeadline_KeepsWithin5To30(int input, int expected)
        => AssemblyAiOptions.ClampDeadline(input).ShouldBe(TimeSpan.FromSeconds(expected));
}
