using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class PromptFormatCapabilitiesTests
{
    [Theory]
    [InlineData("chatml")]
    [InlineData("granite")]
    public void Chat_Formats_Carry_System_Prompt(string format)
        => PromptFormatCapabilities.CarriesSystemPrompt(format).ShouldBeTrue();

    [Fact]
    public void RawIo_Does_Not_Carry_System_Prompt()
        => PromptFormatCapabilities.CarriesSystemPrompt(CleanupPromptFormatter.RawIo)
            .ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-future-format")]
    [InlineData("RAW-IO")] // case-sensitive on purpose, matching Validate()
    public void Unknown_Or_Null_Formats_Are_Treated_As_Carrying(string? format)
        => PromptFormatCapabilities.CarriesSystemPrompt(format).ShouldBeTrue();
}
