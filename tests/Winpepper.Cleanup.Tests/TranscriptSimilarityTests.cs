using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class TranscriptSimilarityTests
{
    [Fact]
    public void ContentWords_DropsFillersAndSelfCorrectionPhrases()
    {
        var words = TranscriptSimilarity.ContentWords(
            "um so like I think we should basically just ship it tomorrow you know");
        // um, like, basically, "you know" removed; "so" kept (not a filler).
        words.ShouldBe(new[] { "so", "i", "think", "we", "should", "just", "ship", "it", "tomorrow" });
    }

    [Fact]
    public void ContentWords_RemovesSelfCorrectionPhrases()
    {
        var words = TranscriptSimilarity.ContentWords(
            "write me a function called add_numbers no wait scratch that call it sum");
        // "no wait" and "scratch that" removed; add_numbers splits on '_'.
        words.ShouldBe(new[] { "write", "me", "a", "function", "called", "add", "numbers", "call", "it", "sum" });
    }

    [Fact]
    public void RetentionRatio_HighWhenCleanedKeepsRawContent()
    {
        var r = TranscriptSimilarity.RetentionRatio(
            "um so like I think we should basically just ship it tomorrow you know",
            "I think we should just ship it tomorrow.");
        r.ShouldBeGreaterThan(0.8);
    }

    [Fact]
    public void RetentionRatio_ZeroOnWholesaleReplacement()
    {
        var r = TranscriptSimilarity.RetentionRatio(
            "who should be fixing this me or the person configuring runpod", "Me");
        r.ShouldBeLessThan(0.2);
    }

    [Fact]
    public void RetentionRatio_OneWhenRawHasNoContentWords()
    {
        // All-filler raw has no content words -> nothing to lose -> 1.0.
        TranscriptSimilarity.RetentionRatio("um uh like you know", "anything").ShouldBe(1.0);
    }

    [Fact]
    public void WordCount_CountsWhitespaceTokens()
    {
        TranscriptSimilarity.WordCount("  Right.  ").ShouldBe(1);
        TranscriptSimilarity.WordCount("output colon forty two").ShouldBe(4);
        TranscriptSimilarity.WordCount("").ShouldBe(0);
    }

    [Fact]
    public void ContentWords_StripsNestedSelfCorrectionPhrase_LongestFirst()
    {
        // Locks the longest-phrase-first ordering: "no let me start over" must be
        // removed as a whole, not leave orphaned tokens from "start over".
        var words = TranscriptSimilarity.ContentWords("no let me start over ship it");
        words.ShouldBe(new[] { "ship", "it" });
    }
}
