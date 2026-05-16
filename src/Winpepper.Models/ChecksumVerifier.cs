using System.Security.Cryptography;

namespace Winpepper.Models;

/// <summary>
/// Streaming SHA-256 helper for large model files. 1 MiB read buffer to keep
/// peak memory bounded.
/// </summary>
public static class ChecksumVerifier
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                            BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static async Task<bool> VerifyAsync(string path, string expectedHexSha256, CancellationToken ct)
    {
        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedHexSha256, StringComparison.OrdinalIgnoreCase);
    }
}
