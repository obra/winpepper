using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Pure formatter contract tests. The prompt shapes below are byte-exact
/// contracts verified against the model's chat templates (see the descriptor
/// research table in the plan): a drifted marker or a stray newline silently
/// degrades cleanup quality on real hardware, so every shape is asserted as a
/// full-string equality, never a Contains.
/// </summary>
public class CleanupPromptFormatterTests
{
    private const string Sys = "SYSTEM-INSTRUCTIONS";
    private const string User = "<USER-INPUT>\nraw words\n</USER-INPUT>";
    private const string Raw = "raw words";

    // ---- chatml: must stay byte-identical to the previous hand-built prompt ----

    [Fact]
    public void ChatMl_PromptText_IsByteIdenticalToLegacyHandBuiltTemplate()
    {
        // This is the EXACT string LlamaCleanupBackend used to hand-build
        // (LlamaCleanupBackend.cs, Bug-3 fix-(iv)). Any drift here changes
        // production behavior for the default qwen model.
        var legacy =
            "<|im_start|>system\n" + Sys + "<|im_end|>\n" +
            "<|im_start|>user\n" + User + "<|im_end|>\n" +
            "<|im_start|>assistant\n";

        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.ChatMl, Sys, User, Raw);

        plan.PromptText.ShouldBe(legacy);
    }

    [Fact]
    public void ChatMl_AntiPrompts_MatchLegacyBackendList()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.ChatMl, Sys, User, Raw);

        plan.AntiPrompts.ShouldBe(new[]
        {
            "<|im_end|>", "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>",
        });
    }

    [Fact]
    public void ChatMl_HasNoGenerationOverrides()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.ChatMl, Sys, User, Raw);

        plan.RepetitionPenalty.ShouldBeNull();
        plan.Greedy.ShouldBeFalse();
        plan.MinNewTokensFloor.ShouldBeNull();
    }

    // ---- granite ----

    [Fact]
    public void Granite_PromptText_UsesRoleMarkup_WithNoTrailingNewlineOnGenPrompt()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.Granite, Sys, User, Raw);

        // Trailing \n after each <|end_of_text|>; NO trailing newline after the
        // assistant generation prompt (verified against granite-4.0's template).
        plan.PromptText.ShouldBe(
            "<|start_of_role|>system<|end_of_role|>" + Sys + "<|end_of_text|>\n" +
            "<|start_of_role|>user<|end_of_role|>" + User + "<|end_of_text|>\n" +
            "<|start_of_role|>assistant<|end_of_role|>");
    }

    [Fact]
    public void Granite_AntiPrompts_StopOnEndOfText_PlusPromptScaffoldExtras()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.Granite, Sys, User, Raw);

        plan.AntiPrompts.ShouldBe(new[]
        {
            "<|end_of_text|>", "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>",
        });
    }

    [Fact]
    public void Granite_HasNoGenerationOverrides()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.Granite, Sys, User, Raw);

        plan.RepetitionPenalty.ShouldBeNull();
        plan.Greedy.ShouldBeFalse();
        plan.MinNewTokensFloor.ShouldBeNull();
    }

    // ---- raw-io ----

    [Fact]
    public void RawIo_PromptText_IsTheExactInputOutputFrame_AroundTheRawTranscript()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.RawIo, Sys, User, Raw);

        // Exact frame; no trailing space after "### Output:". The model was
        // not trained with instructions, so neither the system prompt nor the
        // <USER-INPUT>-wrapped user prompt may appear.
        plan.PromptText.ShouldBe("### Input:\nraw words\n\n### Output:\n");
    }

    [Fact]
    public void RawIo_IgnoresSystemAndUserPrompts_UsesOnlyTheRawTranscript()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.RawIo, Sys, User, Raw);

        plan.PromptText.ShouldNotContain(Sys);
        plan.PromptText.ShouldNotContain("<USER-INPUT>");
    }

    [Fact]
    public void RawIo_TrimsTheRawTranscript_SoTheFrameStaysExact()
    {
        var plan = CleanupPromptFormatter.Build(
            CleanupPromptFormatter.RawIo, Sys, User, "  raw words \n");

        plan.PromptText.ShouldBe("### Input:\nraw words\n\n### Output:\n");
    }

    [Fact]
    public void RawIo_AntiPrompts_StopOnImEnd_AndOnHashHashHash()
    {
        // The "###" stop is REQUIRED: the model sometimes runs into a new
        // "### Input:" block instead of emitting EOS.
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.RawIo, Sys, User, Raw);

        plan.AntiPrompts.ShouldBe(new[] { "<|im_end|>", "###" });
    }

    [Fact]
    public void RawIo_Overrides_GreedyWithRepetitionPenalty_AndMinNewTokensFloor()
    {
        var plan = CleanupPromptFormatter.Build(CleanupPromptFormatter.RawIo, Sys, User, Raw);

        plan.Greedy.ShouldBeTrue();
        plan.RepetitionPenalty.ShouldBe(1.05f);
        plan.MinNewTokensFloor.ShouldBe(900);
    }

    // ---- unknown formats fail loudly ----

    [Theory]
    [InlineData("qwen")]
    [InlineData("ChatML")] // ids are case-sensitive: fail loudly, never guess
    [InlineData("")]
    public void Build_UnknownFormatId_ThrowsWithTheIdAndTheKnownIds(string formatId)
    {
        var ex = Should.Throw<ArgumentException>(
            () => CleanupPromptFormatter.Build(formatId, Sys, User, Raw));

        ex.Message.ShouldContain($"'{formatId}'");
        ex.Message.ShouldContain("chatml");
        ex.Message.ShouldContain("granite");
        ex.Message.ShouldContain("raw-io");
    }

    [Fact]
    public void Validate_UnknownFormatId_Throws_KnownIdsPass()
    {
        Should.Throw<ArgumentException>(() => CleanupPromptFormatter.Validate("nope"));

        CleanupPromptFormatter.Validate(CleanupPromptFormatter.ChatMl);
        CleanupPromptFormatter.Validate(CleanupPromptFormatter.Granite);
        CleanupPromptFormatter.Validate(CleanupPromptFormatter.RawIo);
    }

    // ---- max-new-tokens floor helper ----

    [Fact]
    public void ApplyMinNewTokensFloor_NullFloor_KeepsComputedBudget()
    {
        CleanupPromptFormatter.ApplyMinNewTokensFloor(37, null).ShouldBe(37);
    }

    [Fact]
    public void ApplyMinNewTokensFloor_FloorWins_WhenComputedIsSmaller()
    {
        CleanupPromptFormatter.ApplyMinNewTokensFloor(37, 900).ShouldBe(900);
    }

    [Fact]
    public void ApplyMinNewTokensFloor_ComputedWins_WhenAlreadyAboveFloor()
    {
        CleanupPromptFormatter.ApplyMinNewTokensFloor(2048, 900).ShouldBe(2048);
    }
}
