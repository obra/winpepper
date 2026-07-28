using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelFilesVerifierTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("winpepper-verifier-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private ModelDescriptor MakeInstalledModel(
        byte[] content, string? sha256Override = null, long? sizeOverride = null)
    {
        var dir = Path.Combine(_root, "cleanup", "model-x");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model-x.gguf"), content);
        return new ModelDescriptor
        {
            Name = "model-x",
            Kind = ModelKind.Cleanup,
            DisplayName = "Model X",
            InstallDirRelative = Path.Combine("cleanup", "model-x"),
            Files = new[]
            {
                new ModelFile
                {
                    RelativePath = "model-x.gguf",
                    Url = "",
                    Sha256 = sha256Override ?? Sha256Hex(content),
                    SizeBytes = sizeOverride ?? content.Length,
                },
            },
        };
    }

    [Fact]
    public async Task VerifyAsync_AllFilesPresentWithMatchingSizeAndHash_ReturnsTrue()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3, 4, 5 });

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_FileMissing_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(_root, "cleanup", "model-x", "model-x.gguf"));

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_SizeMismatch_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3 }, sizeOverride: 999);

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_HashMismatch_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(
            new byte[] { 1, 2, 3 }, sha256Override: new string('0', 64));

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }
}
