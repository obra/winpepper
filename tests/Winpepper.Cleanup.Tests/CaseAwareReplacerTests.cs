using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CaseAwareReplacerTests
{
    private static IReadOnlyDictionary<string, string> Dict(params (string K, string V)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Apply_NoReplacements_ReturnsInputUnchanged()
    {
        CaseAwareReplacer.Apply("hello world", Dict()).ShouldBe("hello world");
    }

    [Fact]
    public void Apply_LowercaseMatch_EmitsCanonicalReplacement()
    {
        CaseAwareReplacer.Apply("we tested chat gbt today", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("we tested ChatGPT today");
    }

    [Fact]
    public void Apply_TitleCaseMatch_StillEmitsCanonicalReplacement()
    {
        CaseAwareReplacer.Apply("Chat Gbt is misnamed.", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("ChatGPT is misnamed.");
    }

    [Fact]
    public void Apply_OnlyMatchesWholeWords_NotSubstrings()
    {
        // "chat gbt" must not match "chatgbtstuff" or "prechat gbt".
        CaseAwareReplacer.Apply("chatgbt foo prechat gbt bar chat gbt baz",
                                 Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("chatgbt foo prechat gbt bar ChatGPT baz");
    }

    [Fact]
    public void Apply_MultipleMatches_AllReplaced()
    {
        CaseAwareReplacer.Apply("chat gbt and chat gbt again", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("ChatGPT and ChatGPT again");
    }

    [Fact]
    public void Apply_MultipleRules_AppliedInDeterministicOrder()
    {
        var input = "ann thropic plus chat gbt";
        var result = CaseAwareReplacer.Apply(input, Dict(
            ("chat gbt", "ChatGPT"),
            ("ann thropic", "Anthropic")));
        result.ShouldBe("Anthropic plus ChatGPT");
    }

    [Fact]
    public void Apply_OverlappingRules_LongerWins()
    {
        // "chat gbt model" beats "chat gbt".
        var result = CaseAwareReplacer.Apply("the chat gbt model is here", Dict(
            ("chat gbt", "ChatGPT"),
            ("chat gbt model", "GPT model")));
        result.ShouldBe("the GPT model is here");
    }

    [Fact]
    public void Apply_PunctuationAdjacentMatches_AreReplaced()
    {
        CaseAwareReplacer.Apply("(chat gbt), and chat gbt.", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("(ChatGPT), and ChatGPT.");
    }
}
