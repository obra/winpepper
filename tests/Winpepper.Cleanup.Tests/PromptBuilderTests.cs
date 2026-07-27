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
    public void BuildSystem_AllBlocksPresent()
    {
        var sys = PromptBuilder.BuildSystem(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: "WINDOW");

        sys.ShouldContain("<BASE-PROMPT>\nBASE\n</BASE-PROMPT>");
        sys.ShouldContain("<CORRECTION-HINTS>");
        sys.ShouldContain("- ChatGPT");
        sys.ShouldContain("- chat gbt -> ChatGPT");
        sys.ShouldContain("<OCR-RULES>");
        sys.ShouldContain("<WINDOW-OCR-CONTENT>\nWINDOW\n</WINDOW-OCR-CONTENT>");
        // The system prompt must NOT carry the transcript.
        sys.ShouldNotContain("<USER-INPUT>");
    }

    [Fact]
    public void BuildSystem_NoCorrections_OmitsCorrectionHintsBlock()
    {
        PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, "WINDOW")
            .ShouldNotContain("<CORRECTION-HINTS>");
    }

    [Fact]
    public void BuildSystem_NoWindowContext_OmitsOcrBlocks()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, null);
        sys.ShouldNotContain("<OCR-RULES>");
        sys.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void BuildSystem_EmptyWindowContext_OmitsOcrBlocks()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, "   ");
        sys.ShouldNotContain("<OCR-RULES>");
        sys.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void BuildSystem_PreferredOnly_StillRendersCorrectionHints()
    {
        var sys = PromptBuilder.BuildSystem("BASE", Data(new[] { "ChatGPT" }, replacements: null), null);
        sys.ShouldContain("Preferred transcriptions:");
        sys.ShouldContain("- ChatGPT");
        sys.ShouldNotContain("Misheard replacements:");
    }

    [Fact]
    public void BuildSystem_ReplacementsOnly_StillRendersCorrectionHints()
    {
        var sys = PromptBuilder.BuildSystem("BASE",
            Data(preferred: null, replacements: new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }), null);
        sys.ShouldNotContain("Preferred transcriptions:");
        sys.ShouldContain("Misheard replacements:");
        sys.ShouldContain("- chat gbt -> ChatGPT");
    }

    [Fact]
    public void BuildSystem_TruncatesWindowContext_To4000Chars()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, new string('x', 40_000));
        var start = sys.IndexOf("<WINDOW-OCR-CONTENT>\n", StringComparison.Ordinal) + "<WINDOW-OCR-CONTENT>\n".Length;
        var end = sys.IndexOf("\n</WINDOW-OCR-CONTENT>", StringComparison.Ordinal);
        (end - start).ShouldBeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void BuildSystem_LongWindowContext_ReportsTruncation()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty,
            new string('x', 40_000), out var truncation);

        truncation.Truncated.ShouldBeTrue();
        truncation.OriginalLength.ShouldBe(40_000);
        truncation.RetainedLength.ShouldBe(PromptBuilder.WindowContextMaxChars);
        // The overload must produce the same prompt text as the base method.
        sys.ShouldBe(PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, new string('x', 40_000)));
    }

    [Fact]
    public void BuildSystem_ShortWindowContext_ReportsNoTruncation()
    {
        PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, "WINDOW", out var truncation);
        truncation.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void BuildSystem_NullWindowContext_ReportsNoTruncation()
    {
        PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, null, out var truncation);
        truncation.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void BuildUser_WrapsAndTrimsTranscript()
    {
        PromptBuilder.BuildUser("  hello world  ")
            .ShouldBe("<USER-INPUT>\nhello world\n</USER-INPUT>");
    }
}
