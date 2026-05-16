using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class LearningDiffAnalyzerTests
{
    [Fact]
    public void Accepts_Single_Word_Replacement_Within_Distance_Cap()
    {
        var c = LearningDiffAnalyzer.Analyze(
            injected: "Send chat gbt the link",
            current:  "Send ChatGPT the link");

        c.ShouldNotBeNull();
        c!.Wrong.ShouldBe("chat gbt");
        c.Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void Rejects_Equal_Strings()
    {
        LearningDiffAnalyzer.Analyze("hello world", "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_When_Multiple_Word_Positions_Differ()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "the quick brown fox",
            current:  "a slow brown fox").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Word_Shorter_Than_Min_Length()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "say hi there",
            current:  "say bye there").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Edit_Distance_Above_Sixty_Percent_Of_Word_Length()
    {
        LearningDiffAnalyzer.Analyze("the cat sat", "the dog sat").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Punctuation_Drift_Only()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "hello, world.",
            current:  "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Whitespace_Only_Diff()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "hello  world",
            current:  "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Common_Autocomplete_Capitalization_Of_First_Letter()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "love anthropic stuff",
            current:  "love Anthropic stuff").ShouldBeNull();
    }

    [Fact]
    public void Rejects_When_Diff_Is_Appended_Text_Beyond_Injection()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "hello there",
            current:  "hello there friend").ShouldBeNull();
    }
}
