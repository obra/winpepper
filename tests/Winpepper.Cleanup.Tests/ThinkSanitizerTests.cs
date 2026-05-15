using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class ThinkSanitizerTests
{
    [Theory]
    [InlineData("hello",                                       "hello")]
    [InlineData("<think>thoughts</think>hello",                "hello")]
    [InlineData("before<think>thoughts</think>after",          "beforeafter")]
    [InlineData("<think>multi\nline\nstuff</think>output",     "output")]
    [InlineData("<think>a</think><think>b</think>tail",        "tail")]
    public void Sanitize_StripsBalancedThinkBlocks(string input, string expected)
    {
        ThinkSanitizer.Sanitize(input).ShouldBe(expected);
    }

    [Fact]
    public void Sanitize_OrphanOpeningTag_StripsFromTagToEnd()
    {
        // Model emitted <think> but ran out of tokens before closing it.
        // Per spec §5.5, drop the orphan and everything after.
        ThinkSanitizer.Sanitize("hello<think>started thinking and was cut off")
            .ShouldBe("hello");
    }

    [Fact]
    public void Sanitize_OnlyClosingTag_LeavesUnchanged()
    {
        // No opening tag — leave the (unusual) </think> alone rather than panic.
        ThinkSanitizer.Sanitize("hello</think>world")
            .ShouldBe("hello</think>world");
    }

    [Fact]
    public void Sanitize_TrimsResultingWhitespace()
    {
        ThinkSanitizer.Sanitize("  <think>x</think>  hello  ").ShouldBe("hello");
    }

    [Fact]
    public void Sanitize_PreservesInternalContent_AroundStrippedBlocks()
    {
        ThinkSanitizer.Sanitize("alpha <think>internal</think> beta")
            .ShouldBe("alpha  beta".Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        ThinkSanitizer.Sanitize(input).ShouldBe("");
    }
}
