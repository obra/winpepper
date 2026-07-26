using System.Formats.Tar;
using System.IO.Compression;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class TarGzExtractorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wp-targz-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeArchive(string name = "a.tar.gz")
    {
        // Mimic the real runtime tarball shape: one top-level directory.
        var src = Path.Combine(_dir, "src", "toplevel");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "contract.json"), "{\"version\":\"0.1.3\"}");
        File.WriteAllText(Path.Combine(src, "transcribe.dll"), "fake dll bytes");
        var archive = Path.Combine(_dir, name);
        using var fs = File.Create(archive);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        TarFile.CreateFromDirectory(Path.Combine(_dir, "src"), gz, includeBaseDirectory: false);
        return archive;
    }

    [Fact]
    public void Extracts_archive_contents_and_writes_marker()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "contract.json")));
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "transcribe.dll")));
        Assert.Equal("abc123", File.ReadAllText(archive + ".extracted").Trim());
    }

    [Fact]
    public void Second_call_with_same_hash_is_a_no_op()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        var sentinel = Path.Combine(dest, "toplevel", "extra.txt");
        File.WriteAllText(sentinel, "kept");           // would be wiped by a re-extract
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void Changed_hash_forces_a_clean_re_extract()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        File.WriteAllText(Path.Combine(dest, "toplevel", "stale.txt"), "old");
        TarGzExtractor.EnsureExtracted(archive, dest, "def456");
        Assert.False(File.Exists(Path.Combine(dest, "toplevel", "stale.txt")));
        Assert.Equal("def456", File.ReadAllText(archive + ".extracted").Trim());
    }

    [Fact]
    public void Missing_marker_with_existing_dest_re_extracts()
    {
        var archive = MakeArchive();
        var dest = Path.Combine(_dir, "runtime");
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        File.Delete(archive + ".extracted");
        File.Delete(Path.Combine(dest, "toplevel", "transcribe.dll"));
        TarGzExtractor.EnsureExtracted(archive, dest, "abc123");
        Assert.True(File.Exists(Path.Combine(dest, "toplevel", "transcribe.dll")));
    }
}
