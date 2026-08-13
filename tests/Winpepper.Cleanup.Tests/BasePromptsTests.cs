using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

// Deliberately NO "prompt contains phrase X" tests here: asserting that text
// we wrote contains text we wrote pins wording without testing behavior, and
// blocks prompt editing for no protection. Prompt BEHAVIOR is covered by the
// runner's guards (CleanupRunnerTests) and, eventually, the model-gated eval
// suite (kata ngrv). What remains below are structural invariants tied to
// real regressions and actual routing logic.
public class BasePromptsTests
{
    [Fact]
    public void Default_HasExactlyTwoExamples()
    {
        var p = BasePrompts.Default;
        // Two worked examples, self-correction first and anti-answer question
        // second: with the system/user prompt split that is the measured sweet
        // spot for the 0.5B model (2026-08-12 bake-off, see BasePrompts class
        // comment). Examples are "Input:"/"Output:" lines.
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(2);
        outputs.ShouldBe(2);
    }

    [Fact]
    public void DefaultExampleOutputs_MatchesTheEmbeddedExampleOutputs()
    {
        // Anti-drift: the denylist the runner checks must be exactly the
        // output text shown in the prompt.
        BasePrompts.DefaultExampleOutputs.Count.ShouldBe(2);
        foreach (var exampleOutput in BasePrompts.DefaultExampleOutputs)
        {
            BasePrompts.Default.ShouldContain("Output: " + exampleOutput);
        }
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

    [Fact]
    public void DefaultNoExample_HasNoExample_ButKeepsTheRules()
    {
        // LFM2.5-1.2B echoes the worked example verbatim instead of cleaning
        // (2026-07-27 evidence, see ModelRegistry): the no-example variant must
        // drop the example entirely while keeping the transformation rules and
        // the closing anti-answer reminder.
        var p = BasePrompts.DefaultNoExample;
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(0);
        outputs.ShouldBe(0);
        p.ShouldNotContain("example");
        foreach (var exampleOutput in BasePrompts.DefaultExampleOutputs)
        {
            p.ShouldNotContain(exampleOutput);
        }
        // Same rules and closer as Default (structural anti-drift, not wording pins).
        p.ShouldContain("Apply these transformations:");
        p.ShouldContain("Remember: the USER-INPUT block");
    }

    [Fact]
    public void ForProfile_OmitExample_RoutesOrdinaryAndCustomFallbackToNoExample()
    {
        BasePrompts.ForProfile(CleanupProfile.Ordinary, custom: null, omitExample: true)
            .ShouldBe(BasePrompts.DefaultNoExample);
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: null, omitExample: true)
            .ShouldBe(BasePrompts.DefaultNoExample);
        // Literal has no example; a real custom prompt is used verbatim.
        BasePrompts.ForProfile(CleanupProfile.Literal, custom: null, omitExample: true)
            .ShouldBe(BasePrompts.Literal);
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: "MyPrompt", omitExample: true)
            .ShouldBe("MyPrompt");
    }
}
