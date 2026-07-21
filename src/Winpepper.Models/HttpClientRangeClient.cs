using System.Net.Http.Headers;

namespace Winpepper.Models;

/// <summary>
/// Production <see cref="IHttpRangeClient"/> backed by <see cref="HttpClient"/>.
/// Owns the <see cref="HttpClient"/> instance (do not dispose externally).
/// </summary>
public sealed class HttpClientRangeClient : IHttpRangeClient, IDisposable
{
    private readonly HttpClient _http;

    public HttpClientRangeClient() : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }) { }

    public HttpClientRangeClient(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Winpepper/1.0");
    }

    public async Task<HttpRangeResponse> GetRangeAsync(
        string url, long startByte, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startByte > 0)
        {
            req.Headers.Range = new RangeHeaderValue(startByte, null);
        }
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            var contentStartByte = ValidateResponse(resp, startByte);
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new HttpRangeResponse(stream, contentStartByte, resp);
        }
        catch
        {
            resp.Dispose();
            throw;
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

    private static long ValidateResponse(HttpResponseMessage response, long requestedStartByte)
    {
        if (requestedStartByte > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            return 0;
        }

        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            response.EnsureSuccessStatusCode();
            if (requestedStartByte == 0)
            {
                return 0;
            }

            throw new ModelDownloadException(
                $"Server returned {(int)response.StatusCode} instead of 206 Partial Content for byte range {requestedStartByte}-.");
        }

        var range = response.Content.Headers.ContentRange;
        var validRange = range is not null &&
                         string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) &&
                         range.From == requestedStartByte &&
                         range.To is not null &&
                         range.To >= range.From &&
                         (range.Length is null || range.Length > range.To);
        if (validRange && response.Content.Headers.ContentLength is long contentLength)
        {
            validRange = contentLength == range!.To!.Value - range.From!.Value + 1;
        }

        if (!validRange)
        {
            throw new ModelDownloadException(
                $"Server returned an incompatible Content-Range for byte range {requestedStartByte}-.");
        }

        return requestedStartByte;
    }
}
