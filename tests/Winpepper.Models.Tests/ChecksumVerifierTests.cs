using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ChecksumVerifierTests : IDisposable
{
    private readonly string _dir;
    public ChecksumVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_ReturnsKnownEmptyHash()
    {
        var path = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var hash = await ChecksumVerifier.ComputeSha256Async(path, CancellationToken.None);
        hash.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task ComputeSha256Async_KnownContent()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var hash = await ChecksumVerifier.ComputeSha256Async(path, CancellationToken.None);
        hash.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task VerifyAsync_True_When_HashMatches()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CancellationToken.None);
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_CaseInsensitive_OnExpected()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", CancellationToken.None);
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_False_When_HashMismatches()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "0000000000000000000000000000000000000000000000000000000000000000", CancellationToken.None);
        ok.ShouldBeFalse();
    }
}
