using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ConvergenceTests
{
    [Fact]
    public void SampleStdDev_matches_known_value()
    {
        // values 2,4,4,4,5,5,7,9: mean 5, sample variance 32/7
        Convergence.SampleStdDev(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })
            .ShouldBe(Math.Sqrt(32.0 / 7.0), 1e-9);
    }

    [Fact]
    public void SampleStdDev_is_zero_for_fewer_than_two_values()
    {
        Convergence.SampleStdDev(new double[] { 42 }).ShouldBe(0);
        Convergence.SampleStdDev(Array.Empty<double>()).ShouldBe(0);
    }

    [Fact]
    public void CiHalfWidth95_is_196_sd_over_sqrt_n()
    {
        var values = new double[] { 90, 100, 110 };   // sd = 10, n = 3
        Convergence.CiHalfWidth95(values).ShouldBe(1.96 * 10 / Math.Sqrt(3), 1e-9);
    }

    [Fact]
    public void Median_uses_nearest_rank()
    {
        Convergence.Median(new double[] { 30, 10, 20 }).ShouldBe(20);
        Convergence.Median(new double[] { 10, 20 }).ShouldBe(10); // nearest-rank ceil(0.5*2)-1 = index 0
    }

    [Fact]
    public void Evaluate_first_pass_has_no_previous_and_is_never_stable()
    {
        var p = Convergence.Evaluate(1, new double[] { 100, 100, 100, 100 }, previousMeanMs: 0);
        p.Pass.ShouldBe(1);
        p.MeanMs.ShouldBe(100);
        p.CiHalfWidthMs.ShouldBe(0);
        p.RatioToMean.ShouldBe(0);
        p.DeltaFromPrevious.ShouldBe(double.PositiveInfinity);
        p.Stable.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_small_change_from_previous_pass_is_stable()
    {
        var p = Convergence.Evaluate(2, new double[] { 99, 101 }, previousMeanMs: 100); // mean 100
        p.DeltaFromPrevious.ShouldBe(0);
        p.Stable.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_stability_threshold_is_two_percent_exclusive()
    {
        Convergence.Evaluate(2, new double[] { 102, 102 }, previousMeanMs: 100).Stable.ShouldBeFalse(); // delta 0.02
        Convergence.Evaluate(2, new double[] { 101, 101 }, previousMeanMs: 100).Stable.ShouldBeTrue();  // delta 0.01
    }

    [Fact]
    public void Evaluate_still_reports_ci_diagnostics()
    {
        var p = Convergence.Evaluate(2, new double[] { 90, 100, 110 }, previousMeanMs: 100); // sd 10, n 3
        p.CiHalfWidthMs.ShouldBe(1.96 * 10 / Math.Sqrt(3), 1e-9);
        p.RatioToMean.ShouldBe(1.96 * 10 / Math.Sqrt(3) / 100, 1e-9);
    }

    [Fact]
    public void Evaluate_zero_or_negative_mean_is_never_stable()
    {
        Convergence.Evaluate(2, new double[] { 0, 0, 0 }, previousMeanMs: 100).Stable.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_single_clip_is_never_stable()
    {
        // n < 2: no spread information, must not converge on it
        Convergence.Evaluate(2, new double[] { 100 }, previousMeanMs: 100).Stable.ShouldBeFalse();
    }

    [Fact]
    public void Converged_requires_two_consecutive_stable_points()
    {
        ConvergencePoint P(int pass, bool stable) => new(pass, 100, 1, 0.01, stable ? 0.005 : 0.5, stable);
        Convergence.Converged(new[] { P(1, true) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, false), P(2, true) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, true), P(2, false) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, true), P(2, true) }).ShouldBeTrue();
        Convergence.Converged(new[] { P(1, false), P(2, true), P(3, true) }).ShouldBeTrue();
    }
}
