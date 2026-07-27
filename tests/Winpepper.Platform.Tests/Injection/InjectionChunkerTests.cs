using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class InjectionChunkerTests
{
    [Fact]
    public void Empty_Text_Yields_No_Chunks()
    {
        InjectionChunker.Split(string.Empty, 32).ShouldBeEmpty();
    }

    [Fact]
    public void Short_Text_Is_One_Chunk()
    {
        var chunks = InjectionChunker.Split("hello", 32);
        chunks.ShouldBe(new[] { "hello" });
    }

    [Fact]
    public void Long_Text_Splits_At_ChunkSize()
    {
        var text = new string('a', 70);
        var chunks = InjectionChunker.Split(text, 32);
        chunks.Count.ShouldBe(3);
        chunks[0].Length.ShouldBe(32);
        chunks[1].Length.ShouldBe(32);
        chunks[2].Length.ShouldBe(6);
    }

    [Fact]
    public void Chunks_Reassemble_To_Original()
    {
        var text = "The quick brown fox jumps over the lazy dog. \U0001F600 twice \U0001F600!";
        string.Concat(InjectionChunker.Split(text, 7)).ShouldBe(text);
    }

    [Fact]
    public void Surrogate_Pair_Never_Split_Across_Boundary()
    {
        // 3 BMP chars then an emoji (surrogate pair) straddling a chunkSize=4
        // boundary: the pair's high surrogate lands at index 3, so a naive
        // split at 4 would cut the pair in half.
        var text = "abc\U0001F600def";
        var chunks = InjectionChunker.Split(text, 4);
        foreach (var chunk in chunks)
        {
            char.IsHighSurrogate(chunk[^1]).ShouldBeFalse(
                $"chunk '{chunk}' ends with an unpaired high surrogate");
            char.IsLowSurrogate(chunk[0]).ShouldBeFalse(
                $"chunk '{chunk}' starts with an unpaired low surrogate");
        }
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void NonPositive_ChunkSize_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => InjectionChunker.Split("x", 0));
    }
}
