using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalMetricsTests
{
    [Fact]
    public void Wer_IdenticalAfterNormalization_IsZero()
    {
        var r = EvalMetrics.Wer("Hello, world!", "hello world");

        r.Rate.ShouldBe(0.0);
        r.ReferenceLength.ShouldBe(2);
    }

    [Fact]
    public void Wer_OneSubstitution_IsOneOverReferenceLength()
    {
        var r = EvalMetrics.Wer("the cat sat", "the dog sat");

        r.Substitutions.ShouldBe(1);
        r.Insertions.ShouldBe(0);
        r.Deletions.ShouldBe(0);
        r.Rate.ShouldBe(1.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_PureInsertion_IsCountedAsInsertion()
    {
        // ref: a b   hyp: a x b   -> insert "x" (1 edit over 2 ref words)
        var r = EvalMetrics.Wer("a b", "a x b");

        r.Insertions.ShouldBe(1);
        r.Substitutions.ShouldBe(0);
        r.Deletions.ShouldBe(0);
        r.Rate.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_MixedEdits_TotalEditCountIsMinimal()
    {
        // ref: a b c   hyp: a x b   -> two optimal alignments exist (insert+delete
        // or two substitutions); either way the minimal edit count is 2.
        var r = EvalMetrics.Wer("a b c", "a x b");

        r.Edits.ShouldBe(2);
        r.Rate.ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_DroppedFillerWord_CountsAsDeletion()
    {
        // References keep disfluencies; a model that drops "um" pays for it.
        var r = EvalMetrics.Wer("um hello", "hello");

        r.Deletions.ShouldBe(1);
        r.Rate.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_EmptyReference_NonEmptyHypothesis_IsOne()
    {
        EvalMetrics.Wer("", "hello there").Rate.ShouldBe(1.0);
    }

    [Fact]
    public void Wer_EmptyReferenceAndHypothesis_IsZero()
    {
        EvalMetrics.Wer("", "...").Rate.ShouldBe(0.0); // "..." normalizes to empty
    }

    [Fact]
    public void Cer_SingleCharacterError_IsOneOverReferenceChars()
    {
        var r = EvalMetrics.Cer("abc", "abd");

        r.Rate.ShouldBe(1.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void SilentPass_PunctuationOrEmpty_IsTrue_WordsAreFalse()
    {
        EvalMetrics.SilentPass("").ShouldBeTrue();
        EvalMetrics.SilentPass(" . , ! ").ShouldBeTrue();
        EvalMetrics.SilentPass("hm").ShouldBeFalse();
    }
}
