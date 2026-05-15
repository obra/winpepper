using Shouldly;
using Winpepper.Core.Io;
using Xunit;

namespace Winpepper.Core.Tests.Io;

public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"winpepper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void WriteAllText_CreatesFileWithContents()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        AtomicFile.WriteAllText(path, "hello");
        File.ReadAllText(path).ShouldBe("hello");
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        File.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        File.ReadAllText(path).ShouldBe("new");
    }

    [Fact]
    public void WriteAllText_DoesNotLeaveTempFile()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        AtomicFile.WriteAllText(path, "content");
        Directory.GetFiles(_tempDir).Length.ShouldBe(1);
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectories()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "file.txt");
        AtomicFile.WriteAllText(path, "hello");
        File.ReadAllText(path).ShouldBe("hello");
    }
}
