using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class PromptBuilderTests
{
    private static CorrectionsData Data(IReadOnlyList<string>? preferred = null,
                                        IReadOnlyDictionary<string, string>? replacements = null) =>
        new()
        {
            Preferred = preferred ?? Array.Empty<string>(),
            Replacements = replacements ?? new Dictionary<string, string>(),
        };

    [Fact]
    public void Build_AllBlocksPresent_JoinsWithBlankLines()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: "WINDOW",
            userInput: "raw transcript");

        prompt.ShouldContain("<BASE-PROMPT>\nBASE\n</BASE-PROMPT>");
        prompt.ShouldContain("<CORRECTION-HINTS>");
        prompt.ShouldContain("- ChatGPT");
        prompt.ShouldContain("- chat gbt -> ChatGPT");
        prompt.ShouldContain("<OCR-RULES>");
        prompt.ShouldContain("<WINDOW-OCR-CONTENT>\nWINDOW\n</WINDOW-OCR-CONTENT>");
        prompt.ShouldContain("<USER-INPUT>\nraw transcript\n</USER-INPUT>");
        prompt.ShouldContain("\n\n"); // blocks separated by blank lines
    }

    [Fact]
    public void Build_NoCorrections_OmitsCorrectionHintsBlock()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: "WINDOW",
            userInput: "x");

        prompt.ShouldNotContain("<CORRECTION-HINTS>");
    }

    [Fact]
    public void Build_NoWindowContext_OmitsOcrBlocks()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: null,
            userInput: "x");

        prompt.ShouldNotContain("<OCR-RULES>");
        prompt.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void Build_EmptyWindowContext_OmitsOcrBlocks()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: "   ",
            userInput: "x");

        prompt.ShouldNotContain("<OCR-RULES>");
        prompt.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void Build_PreferredOnly_StillRendersCorrectionHints()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, replacements: null),
            windowContext: null,
            userInput: "x");

        prompt.ShouldContain("Preferred transcriptions:");
        prompt.ShouldContain("- ChatGPT");
        prompt.ShouldNotContain("Misheard replacements:");
    }

    [Fact]
    public void Build_ReplacementsOnly_StillRendersCorrectionHints()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(preferred: null, replacements: new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: null,
            userInput: "x");

        prompt.ShouldNotContain("Preferred transcriptions:");
        prompt.ShouldContain("Misheard replacements:");
        prompt.ShouldContain("- chat gbt -> ChatGPT");
    }

    [Fact]
    public void Build_TruncatesWindowContext_To4000Chars()
    {
        var long40k = new string('x', 40_000);
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: long40k,
            userInput: "x");

        // <WINDOW-OCR-CONTENT>\n{<=4000 chars}\n</WINDOW-OCR-CONTENT>
        var start = prompt.IndexOf("<WINDOW-OCR-CONTENT>\n", StringComparison.Ordinal) + "<WINDOW-OCR-CONTENT>\n".Length;
        var end = prompt.IndexOf("\n</WINDOW-OCR-CONTENT>", StringComparison.Ordinal);
        (end - start).ShouldBeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void Build_TrimsRawTranscript()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: null,
            userInput: "  hello world  ");

        prompt.ShouldContain("<USER-INPUT>\nhello world\n</USER-INPUT>");
    }
}
