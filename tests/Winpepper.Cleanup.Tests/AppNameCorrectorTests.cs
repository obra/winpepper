using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class AppNameCorrectorTests
{
    [Fact]
    public void Apply_SentenceInitialContext_CapitalizesReplacement()
    {
        // Observed ASR case: "testing winpepper" heard as "Testing wheat pepper".
        // Preceding word "Testing" is capitalized -> "Winpepper".
        AppNameCorrector.Apply("Testing wheat pepper. How's it going?")
            .ShouldBe("Testing Winpepper. How's it going?");
    }

    [Fact]
    public void Apply_MidSentenceLowercaseContext_LowercasesReplacement()
    {
        // Preceding word "shipped" is lowercase -> "winpepper".
        AppNameCorrector.Apply("i shipped win pepper today")
            .ShouldBe("i shipped winpepper today");
    }

    [Theory]
    [InlineData("wheat pepper")]
    [InlineData("win pepper")]
    [InlineData("wind pepper")]
    [InlineData("when pepper")]
    public void Apply_AllKnownMishearings_AreCorrected(string mishearing)
    {
        // Start-of-string is sentence-initial -> capitalized.
        AppNameCorrector.Apply($"{mishearing} rocks").ShouldBe("Winpepper rocks");
    }

    [Fact]
    public void Apply_AfterSentencePunctuation_CapitalizesReplacement()
    {
        AppNameCorrector.Apply("Done. wheat pepper wins.").ShouldBe("Done. Winpepper wins.");
    }

    [Fact]
    public void Apply_CollateralCulinaryPhrase_IsAlsoCorrected()
    {
        // Documented tradeoff: a genuine "wheat pepper" phrase is corrected too.
        // Accepted collateral — the app name is dictated far more often, and the
        // rule stays conservative (fixed list, no general vocabulary system).
        AppNameCorrector.Apply("wheat pepper soup recipe").ShouldBe("Winpepper soup recipe");
    }

    [Fact]
    public void Apply_NoMishearing_ReturnsTextUnchanged()
    {
        AppNameCorrector.Apply("just some ordinary sentence").ShouldBe("just some ordinary sentence");
    }

    [Fact]
    public void Apply_DoesNotMatchAcrossWordBoundaries()
    {
        // "unwheat pepper" must not match ("wheat" is not a whole word here).
        AppNameCorrector.Apply("unwheat peppery").ShouldBe("unwheat peppery");
    }

    [Fact]
    public void Apply_EmptyString_ReturnsEmpty()
    {
        AppNameCorrector.Apply("").ShouldBe("");
    }
}
