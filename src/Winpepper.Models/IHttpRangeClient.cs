namespace Winpepper.Models;

/// <summary>
/// Minimal HTTP range-request surface used by <see cref="ModelDownloader"/>.
/// The production implementation wraps <see cref="System.Net.Http.HttpClient"/>.
/// Tests substitute a fake to avoid network IO.
/// </summary>
public interface IHttpRangeClient
{
    /// <summary>
    /// Open bytes from <paramref name="url"/> starting at <paramref name="startByte"/>.
    /// Implementations issue <c>Range: bytes=startByte-</c> when startByte &gt; 0
    /// and report whether the server honored that request.
    /// </summary>
    Task<HttpRangeResponse> GetRangeAsync(string url, long startByte, CancellationToken ct);

    /// <summary>Returns the full content length via a HEAD or 0-range GET.</summary>
    Task<long> GetContentLengthAsync(string url, CancellationToken ct);
}

/// <summary>
/// An opened HTTP body and the byte offset represented by its first byte.
/// A zero offset after a nonzero request means the server ignored the Range header.
/// </summary>
public sealed class HttpRangeResponse : IAsyncDisposable
{
    private readonly IDisposable? _owner;

    public HttpRangeResponse(Stream content, long contentStartByte, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegative(contentStartByte);
        Content = content;
        ContentStartByte = contentStartByte;
        _owner = owner;
    }

    public Stream Content { get; }
    public long ContentStartByte { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Content.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _owner?.Dispose();
        }
    }
}
