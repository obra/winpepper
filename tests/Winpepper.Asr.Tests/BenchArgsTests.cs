using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class BenchArgsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ValidateRepeats_AtLeastOne_IsValid(int repeats)
        => BenchArgs.ValidateRepeats(repeats).ShouldBeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void ValidateRepeats_BelowOne_YieldsClearError(int repeats)
    {
        var error = BenchArgs.ValidateRepeats(repeats);

        error.ShouldNotBeNull();
        error.ShouldContain("--repeats must be >= 1");
        error.ShouldContain(repeats.ToString());
    }
}
