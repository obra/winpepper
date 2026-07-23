using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public sealed class InjectionTextTests
{
    [Theory]
    // Ends with a period -> trailing space appended for paste ergonomics.
    [InlineData("Hello world.", "Hello world. ")]
    [InlineData("One. Two.", "One. Two. ")]
    [InlineData("Ends with ellipsis...", "Ends with ellipsis... ")]
    // No trailing period -> unchanged.
    [InlineData("Hello world", "Hello world")]
    [InlineData("A question?", "A question?")]
    [InlineData("Loud!", "Loud!")]
    [InlineData("Already spaced. ", "Already spaced. ")]
    [InlineData("", "")]
    public void ForPaste_AppendsSpaceOnlyAfterTrailingPeriod(string input, string expected)
        => InjectionText.ForPaste(input).ShouldBe(expected);
}
