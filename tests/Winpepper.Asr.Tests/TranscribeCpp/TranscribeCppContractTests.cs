using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public class TranscribeCppContractTests
{
    private const string GoodJson =
        "{\"version\":\"0.1.3\",\"header_hash\":\"86b16dd97ad1cb58\",\"backends\":[\"vulkan\",\"cpu\"],\"lane\":\"cpu-vulkan\"}";

    [Fact]
    public void Parses_the_real_v013_contract_and_is_compatible()
    {
        var c = TranscribeCppContract.Parse(GoodJson);
        Assert.Equal("0.1.3", c.Version);
        Assert.Equal("86b16dd97ad1cb58", c.HeaderHash);
        Assert.True(c.IsCompatible);
    }

    [Fact]
    public void Wrong_version_is_incompatible()
        => Assert.False(TranscribeCppContract.Parse(
            "{\"version\":\"0.2.0\",\"header_hash\":\"86b16dd97ad1cb58\"}").IsCompatible);

    [Fact]
    public void Wrong_header_hash_is_incompatible()
        => Assert.False(TranscribeCppContract.Parse(
            "{\"version\":\"0.1.3\",\"header_hash\":\"deadbeefdeadbeef\"}").IsCompatible);

    [Fact]
    public void Missing_fields_throw_a_clear_error()
        => Assert.Throws<TranscribeCppException>(() => TranscribeCppContract.Parse("{}"));

    [Fact]
    public void Garbage_json_throws_a_clear_error()
        => Assert.Throws<TranscribeCppException>(() => TranscribeCppContract.Parse("not json"));
}
