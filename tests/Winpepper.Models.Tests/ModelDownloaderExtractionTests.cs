using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDownloaderExtractionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wp-dl-").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // Mirrors the SyncProgress helper in ModelDownloaderTests: reports synchronously
    // so assertions can run immediately after DownloadAsync completes.
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static (byte[] Bytes, string Sha256) MakeArchiveBytes()
    {
        var tmp = Directory.CreateTempSubdirectory("wp-arc-").FullName;
        try
        {
            var top = Path.Combine(tmp, "toplevel");
            Directory.CreateDirectory(top);
            File.WriteAllText(Path.Combine(top, "contract.json"), "{}");
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                TarFile.CreateFromDirectory(tmp, gz, includeBaseDirectory: false);
            var bytes = ms.ToArray();
            return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        finally { Directory.Delete(tmp, true); }
    }

    private static ModelDescriptor BuildDescriptor(byte[] bytes, string sha)
    {
        return new ModelDescriptor
        {
            Name = "test-runtime", Kind = ModelKind.Asr, DisplayName = "t",
            InstallDirRelative = "test-runtime",
            Files = new[]
            {
                new ModelFile
                {
                    RelativePath = "native.tar.gz",
                    Url = "https://example.invalid/native.tar.gz",
                    Sha256 = sha, SizeBytes = bytes.Length,
                    ExtractToRelative = "runtime",
                },
            },
        };
    }

    [Fact]
    public async Task Downloaded_archive_with_ExtractToRelative_is_extracted()
    {
        var (bytes, sha) = MakeArchiveBytes();
        var descriptor = BuildDescriptor(bytes, sha);
        // Reuse the existing FakeRangeClient from ModelDownloaderTests.cs
        // (parameterless ctor; bodies registered per-URL via SetBody).
        var fake = new FakeRangeClient();
        fake.SetBody("https://example.invalid/native.tar.gz", bytes);
        var downloader = new ModelDownloader(fake);
        await downloader.DownloadAsync(descriptor, _root,
            new Progress<DownloadProgress>(), CancellationToken.None);

        var modelDir = Path.Combine(_root, "test-runtime");
        Assert.True(File.Exists(Path.Combine(modelDir, "native.tar.gz")));           // archive kept
        Assert.True(File.Exists(Path.Combine(modelDir, "runtime", "toplevel", "contract.json")));
        Assert.Equal(sha, File.ReadAllText(Path.Combine(modelDir, "native.tar.gz.extracted")).Trim());
    }

    [Fact]
    public async Task Extraction_failure_reports_Failed_phase_and_propagates()
    {
        // Garbage bytes (not a valid gzip stream) with a matching descriptor:
        // download and checksum succeed, extraction throws.
        var bytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 };
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var descriptor = BuildDescriptor(bytes, sha);
        var fake = new FakeRangeClient();
        fake.SetBody("https://example.invalid/native.tar.gz", bytes);
        var downloader = new ModelDownloader(fake);
        var reports = new List<DownloadProgress>();

        var ex = await Record.ExceptionAsync(() => downloader.DownloadAsync(
            descriptor, _root, new SyncProgress<DownloadProgress>(reports.Add), CancellationToken.None));

        Assert.NotNull(ex); // extraction failure must still propagate
        var failed = Assert.Single(reports, r => r.Phase == DownloadPhase.Failed);
        Assert.Equal("native.tar.gz", failed.FileRelativePath);
        Assert.False(string.IsNullOrEmpty(failed.ErrorMessage));
    }

    [Fact]
    public async Task Already_installed_archive_with_missing_extraction_is_healed()
    {
        var (bytes, sha) = MakeArchiveBytes();
        var descriptor = BuildDescriptor(bytes, sha);
        var modelDir = Path.Combine(_root, "test-runtime");
        Directory.CreateDirectory(modelDir);
        await File.WriteAllBytesAsync(Path.Combine(modelDir, "native.tar.gz"), bytes); // pre-installed, never extracted

        var fake = new FakeRangeClient();
        fake.SetBody("https://example.invalid/native.tar.gz", bytes);
        var downloader = new ModelDownloader(fake);
        await downloader.DownloadAsync(descriptor, _root,
            new Progress<DownloadProgress>(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(modelDir, "runtime", "toplevel", "contract.json")));
    }
}
