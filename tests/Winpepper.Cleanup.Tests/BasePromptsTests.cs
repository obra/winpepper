using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class BasePromptsTests
{
    [Fact]
    public void Default_MentionsFillerWords()
    {
        var p = BasePrompts.Default;
        // Each of these filler words must appear in the default prompt per §6.3.
        foreach (var filler in new[] { "um", "uh", "like", "you know", "basically", "literally", "sort of", "kind of" })
            p.ShouldContain(filler, Case.Sensitive);
    }

    [Fact]
    public void Default_MentionsSelfCorrectionCommands()
    {
        var p = BasePrompts.Default;
        p.ShouldContain("scratch that");
        p.ShouldContain("never mind");
        p.ShouldContain("start over");
    }

    [Fact]
    public void Default_RequiresFullTranscriptReproduction()
    {
        var p = BasePrompts.Default;
        p.ShouldContain("never summarize", Case.Insensitive);
    }

    [Fact]
    public void Default_HasThreeExamples()
    {
        var p = BasePrompts.Default;
        // Examples are blocks starting with "Input:" / "Output:".
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(3);
        outputs.ShouldBe(3);
    }

    [Fact]
    public void Literal_DisablesFillerRemoval()
    {
        BasePrompts.Literal.ShouldContain("do not remove filler", Case.Insensitive);
        BasePrompts.Literal.ShouldContain("punctuation", Case.Insensitive);
    }

    [Fact]
    public void ForProfile_DefaultsRouteCorrectly()
    {
        BasePrompts.ForProfile(CleanupProfile.Ordinary, custom: null).ShouldBe(BasePrompts.Default);
        BasePrompts.ForProfile(CleanupProfile.Literal,  custom: null).ShouldBe(BasePrompts.Literal);
    }

    [Fact]
    public void ForProfile_Custom_UsesProvidedText()
    {
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: "MyPrompt").ShouldBe("MyPrompt");
    }

    [Fact]
    public void ForProfile_Custom_FallsBackToDefault_OnNullOrWhitespace()
    {
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: null).ShouldBe(BasePrompts.Default);
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: "   ").ShouldBe(BasePrompts.Default);
    }
}
