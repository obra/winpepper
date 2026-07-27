using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class EvalPassesTests
{
    [Fact]
    public void Summarize_computes_percentiles_cpu_memory_rtf_wer()
    {
        var s = EvalPasses.Summarize(
            pass: 2,
            latenciesMs: new double[] { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000 },
            rtfs: new double[] { 0.1, 0.3 },
            wers: new double[] { 0.10, 0.20 },
            cpuSeconds: 12.3456,
            peakWorkingSetBytes: 2L * 1024 * 1024 * 1024,
            failedCount: 1);
        s.Pass.ShouldBe(2);
        s.LatencyP50Ms.ShouldBe(500);   // nearest-rank ceil(0.5*10)-1 = index 4
        s.LatencyP90Ms.ShouldBe(900);   // ceil(0.9*10)-1 = index 8
        s.LatencyMaxMs.ShouldBe(1000);
        s.CpuSeconds.ShouldBe(12.346);  // rounded to 3 decimals
        s.PeakMemoryMb.ShouldBe(2048.0);
        s.MeanRtf.ShouldBe(0.2);
        s.MeanWer.ShouldBe(0.15);
        s.FailedCount.ShouldBe(1);
    }

    [Fact]
    public void Summarize_handles_empty_inputs()
    {
        var s = EvalPasses.Summarize(1, Array.Empty<double>(), Array.Empty<double>(),
            Array.Empty<double>(), 0, 0, 0);
        s.LatencyP50Ms.ShouldBe(0);
        s.LatencyMaxMs.ShouldBe(0);
        s.MeanRtf.ShouldBe(0);
        s.MeanWer.ShouldBeNull();
    }

    [Fact]
    public void Summarize_sorts_latencies_itself()
    {
        var s = EvalPasses.Summarize(1, new double[] { 900, 100, 500 },
            Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0);
        s.LatencyP50Ms.ShouldBe(500);
        s.LatencyMaxMs.ShouldBe(900);
    }
}
