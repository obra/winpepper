using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class VocabularyTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void FromFile_ReadsAllTokens_InOrder()
    {
        var v = Vocabulary.FromFile(FixturePath("tiny-vocab.txt"));
        v.Size.ShouldBe(5);
        v.Tokens.ShouldBe(["▁hello", "▁world", ",", ".", "<blank>"]);
        v.BlankId.ShouldBe(4);
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, "hello world")]
    [InlineData(new[] { 0, 2, 1, 3 }, "hello, world.")]
    [InlineData(new int[] { }, "")]
    public void Decode_ExpandsBoundary_AndStripsBlanks(int[] tokenIds, string expected)
    {
        var v = Vocabulary.FromFile(FixturePath("tiny-vocab.txt"));
        v.Decode(tokenIds).ShouldBe(expected);
    }
}
