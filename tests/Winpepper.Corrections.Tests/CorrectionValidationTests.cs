using Shouldly;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionValidationTests
{
    [Theory]
    [InlineData("ChatGPT", true)]
    [InlineData("ab", true)]                  // min length 2
    [InlineData("a", false)]                  // too short
    [InlineData("", false)]                   // empty
    [InlineData("   ", false)]                // whitespace-only
    public void ValidatePreferred_AppliesLengthAndWhitespaceRules(string value, bool expected)
    {
        CorrectionValidation.IsValidPreferred(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("chat gbt", "ChatGPT", true)]
    [InlineData("ab", "cd", true)]
    [InlineData("a", "ChatGPT", false)]            // wrong side too short
    [InlineData("chat gbt", "a", false)]           // right side too short
    [InlineData("chat gbt", "chat gbt", false)]    // self-mapping
    [InlineData("ChatGPT", "chatgpt", true)]       // case differences are allowed
    [InlineData("  chat gbt  ", "ChatGPT", false)] // leading/trailing whitespace banned
    [InlineData("", "ChatGPT", false)]
    [InlineData("chat gbt", "", false)]
    public void ValidateReplacement_AppliesAllRules(string wrong, string right, bool expected)
    {
        CorrectionValidation.IsValidReplacement(wrong, right).ShouldBe(expected);
    }
}
