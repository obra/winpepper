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
