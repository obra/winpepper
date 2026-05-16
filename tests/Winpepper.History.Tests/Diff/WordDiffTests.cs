using Shouldly;
using Winpepper.History.Diff;
using Xunit;

namespace Winpepper.History.Tests.Diff;

public class WordDiffTests
{
    [Fact]
    public void Identical_Strings_AllEqual()
    {
        var segments = WordDiff.Compute("hello world", "hello world");
        segments.ShouldAllBe(s => s.Kind == WordDiffKind.Equal);
        string.Concat(segments.Select(s => s.Text)).ShouldBe("hello world");
    }

    [Fact]
    public void Single_Word_Substitution()
    {
        var segments = WordDiff.Compute("hello world", "hello earth");
        segments.Count(s => s.Kind == WordDiffKind.Equal).ShouldBeGreaterThanOrEqualTo(1);
        segments.Any(s => s.Kind == WordDiffKind.Delete && s.Text.Contains("world")).ShouldBeTrue();
        segments.Any(s => s.Kind == WordDiffKind.Insert && s.Text.Contains("earth")).ShouldBeTrue();
    }

    [Fact]
    public void Empty_Original_All_Inserts()
    {
        var segments = WordDiff.Compute("", "anything goes");
        segments.ShouldAllBe(s => s.Kind == WordDiffKind.Insert || s.Kind == WordDiffKind.Equal && string.IsNullOrEmpty(s.Text));
        string.Concat(segments.Where(s => s.Kind == WordDiffKind.Insert).Select(s => s.Text))
              .ShouldContain("anything");
    }

    [Fact]
    public void Empty_Rerun_All_Deletes()
    {
        var segments = WordDiff.Compute("anything goes", "");
        string.Concat(segments.Where(s => s.Kind == WordDiffKind.Delete).Select(s => s.Text))
              .ShouldContain("anything");
    }

    [Fact]
    public void Stable_Reconstruction_OfOriginal_And_Rerun()
    {
        var original = "the quick brown fox jumps";
        var rerun    = "the slow brown fox leaps over";
        var segments = WordDiff.Compute(original, rerun);

        var reconstructedOriginal = string.Concat(segments
            .Where(s => s.Kind == WordDiffKind.Equal || s.Kind == WordDiffKind.Delete)
            .Select(s => s.Text));
        var reconstructedRerun = string.Concat(segments
            .Where(s => s.Kind == WordDiffKind.Equal || s.Kind == WordDiffKind.Insert)
            .Select(s => s.Text));

        reconstructedOriginal.Trim().ShouldBe(original);
        reconstructedRerun.Trim().ShouldBe(rerun);
    }
}
