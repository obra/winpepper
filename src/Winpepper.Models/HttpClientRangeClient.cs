using System.Net.Http.Headers;

namespace Winpepper.Models;

/// <summary>
/// Production <see cref="IHttpRangeClient"/> backed by <see cref="HttpClient"/>.
/// Owns the <see cref="HttpClient"/> instance (do not dispose externally).
/// </summary>
public sealed class HttpClientRangeClient : IHttpRangeClient, IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly HttpClient _http;

    public HttpClientRangeClient() : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }) { }

    public HttpClientRangeClient(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Winpepper/1.0");
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(
        string url, long startByte,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startByte > 0)
        {
            req.Headers.Range = new RangeHeaderValue(startByte, null);
        }
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[CopyBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            yield return new ReadOnlyMemory<byte>(buf, 0, read);
        }
    }

    public async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return resp.Content.Headers.ContentLength ?? -1;
    }

    public void Dispose() => _http.Dispose();
}
