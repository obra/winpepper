using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDescriptorTests
{
    [Fact]
    public void IsFullyInstalled_True_When_AllFilesExistAndAreNonZero()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 5 },
                new ModelFile { RelativePath = "b.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 4 },
            },
        };
        var installRoot = temp.Path;
        Directory.CreateDirectory(Path.Combine(installRoot, "test"));
        File.WriteAllText(Path.Combine(installRoot, "test", "a.bin"), "hello");
        File.WriteAllText(Path.Combine(installRoot, "test", "b.bin"), "abcd");
        d.IsFullyInstalled(installRoot).ShouldBeTrue();
    }

    [Fact]
    public void IsFullyInstalled_False_When_AnyFileMissing()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "u", Sha256 = "h", SizeBytes = 5 },
                new ModelFile { RelativePath = "b.bin", Url = "u", Sha256 = "h", SizeBytes = 5 },
            },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "test"));
        File.WriteAllText(Path.Combine(temp.Path, "test", "a.bin"), "hello");
        d.IsFullyInstalled(temp.Path).ShouldBeFalse();
    }

    [Fact]
    public void IsFullyInstalled_False_When_FileEmpty()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[] { new ModelFile { RelativePath = "a.bin", Url = "u", Sha256 = "h", SizeBytes = 5 } },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "test"));
        File.WriteAllText(Path.Combine(temp.Path, "test", "a.bin"), "");
        d.IsFullyInstalled(temp.Path).ShouldBeFalse();
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"models-test-{Guid.NewGuid():N}");
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
