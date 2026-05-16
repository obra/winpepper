using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class LevenshteinDistanceTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "", 0)]
    [InlineData("a", "", 1)]
    [InlineData("", "a", 1)]
    [InlineData("chat gbt", "ChatGPT", 4)]
    [InlineData("equal", "equal", 0)]
    public void Compute_Matches_Reference_Values(string a, string b, int expected)
    {
        LevenshteinDistance.Compute(a, b).ShouldBe(expected);
    }
}
