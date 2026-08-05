using System.Formats.Tar;
using System.IO.Compression;
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
    public void Defaults_PromptFormatIsChatMl_AndNotManualInstallOnly()
    {
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Cleanup,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = Array.Empty<ModelFile>(),
        };

        d.PromptFormat.ShouldBe("chatml");
        d.ManualInstallOnly.ShouldBeFalse();
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

    [Fact]
    public void IsFullyInstalledAndExtracted_Equals_IsFullyInstalled_When_No_Archive_Files()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "plain", Kind = ModelKind.Asr, DisplayName = "Plain",
            InstallDirRelative = "plain",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 5 },
            },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "plain"));
        File.WriteAllText(Path.Combine(temp.Path, "plain", "a.bin"), "hello");

        d.IsFullyInstalled(temp.Path).ShouldBeTrue();
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeTrue();
    }

    [Fact]
    public void IsFullyInstalledAndExtracted_False_When_Archive_Present_But_Not_Extracted()
    {
        using var temp = new TempDir();
        var d = MakeArchiveDescriptor(temp, out _);

        // Broken-but-present: files exist and are non-empty, but nothing was
        // ever extracted. The weak check says installed; the strong one must not.
        d.IsFullyInstalled(temp.Path).ShouldBeTrue();
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeFalse();
    }

    [Fact]
    public void IsFullyInstalledAndExtracted_True_After_Extraction_And_False_After_Tree_Deleted()
    {
        using var temp = new TempDir();
        var d = MakeArchiveDescriptor(temp, out var archivePath);
        var runtimeDir = Path.Combine(temp.Path, "streamy", "runtime");

        TarGzExtractor.EnsureExtracted(archivePath, runtimeDir, "cafebabe");
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeTrue();

        Directory.Delete(runtimeDir, recursive: true);
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeFalse();
    }

    /// <summary>Descriptor with one plain file and one archive file (ExtractToRelative
    /// = "runtime"), both present on disk; the archive is a real (tiny) tar.gz so
    /// EnsureExtracted can extract it. Sha256 "cafebabe" is arbitrary — IsExtracted
    /// compares it to the marker file EnsureExtracted writes, not to a real hash.</summary>
    private static ModelDescriptor MakeArchiveDescriptor(TempDir temp, out string archivePath)
    {
        var dir = Path.Combine(temp.Path, "streamy");
        var src = Path.Combine(temp.Path, "src", "toplevel");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(dir, "model.gguf"), "weights");
        File.WriteAllText(Path.Combine(src, "transcribe.dll"), "fake dll bytes");
        archivePath = Path.Combine(dir, "runtime.tar.gz");
        using (var fs = File.Create(archivePath))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(Path.Combine(temp.Path, "src"), gz, includeBaseDirectory: false);
        }

        return new ModelDescriptor
        {
            Name = "streamy", Kind = ModelKind.StreamingAsr, DisplayName = "Streamy",
            InstallDirRelative = "streamy",
            Files = new[]
            {
                new ModelFile { RelativePath = "model.gguf", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 7 },
                new ModelFile { RelativePath = "runtime.tar.gz", Url = "https://x", Sha256 = "cafebabe", SizeBytes = 1, ExtractToRelative = "runtime" },
            },
        };
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"models-test-{Guid.NewGuid():N}");
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
