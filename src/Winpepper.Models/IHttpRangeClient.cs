namespace Winpepper.Models;

/// <summary>
/// Minimal HTTP range-request surface used by <see cref="ModelDownloader"/>.
/// The production implementation wraps <see cref="System.Net.Http.HttpClient"/>.
/// Tests substitute a fake to avoid network IO.
/// </summary>
public interface IHttpRangeClient
{
    /// <summary>
    /// Stream bytes from <paramref name="url"/> starting at <paramref name="startByte"/>.
    /// Implementations must issue <c>Range: bytes=startByte-</c> when startByte &gt; 0.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(string url, long startByte, CancellationToken ct);

    /// <summary>Returns the full content length via a HEAD or 0-range GET.</summary>
    Task<long> GetContentLengthAsync(string url, CancellationToken ct);
}
