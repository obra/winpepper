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
    public void Default_HasExactlyOneExample()
    {
        var p = BasePrompts.Default;
        // A single worked example keeps a 0.5B model from pattern-completing a
        // few-shot block (spec fix-(iv)). Examples are "Input:"/"Output:" lines.
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(1);
        outputs.ShouldBe(1);
    }

    [Fact]
    public void DefaultExampleOutputs_MatchesTheEmbeddedExampleOutput()
    {
        // Anti-drift: the denylist the runner checks must be exactly the
        // output text shown in the prompt.
        BasePrompts.DefaultExampleOutputs.Count.ShouldBe(1);
        BasePrompts.Default.ShouldContain("Output: " + BasePrompts.DefaultExampleOutputs[0]);
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
