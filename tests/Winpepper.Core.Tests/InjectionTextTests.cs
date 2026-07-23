using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public sealed class InjectionTextTests
{
    [Theory]
    // Ends with sentence-final punctuation -> trailing space appended.
    [InlineData("Hello world.", "Hello world. ")]
    [InlineData("One. Two.", "One. Two. ")]
    [InlineData("Ends with ellipsis...", "Ends with ellipsis... ")]
    [InlineData("A question?", "A question? ")]
    [InlineData("Loud!", "Loud! ")]
    [InlineData("Really?!", "Really?! ")]
    // No sentence-final punctuation at the end -> unchanged.
    [InlineData("Hello world", "Hello world")]
    [InlineData("comma,", "comma,")]
    [InlineData("colon:", "colon:")]
    [InlineData("Already spaced. ", "Already spaced. ")]
    [InlineData("Already spaced? ", "Already spaced? ")]
    [InlineData("", "")]
    public void ForPaste_AppendsSpaceOnlyAfterSentenceFinalPunctuation(string input, string expected)
        => InjectionText.ForPaste(input).ShouldBe(expected);
}
