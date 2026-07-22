using System.Net;
using System.Net.Http.Headers;
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class HttpClientRangeClientTests
{
    [Fact]
    public async Task GetRangeAsync_IgnoredRange_ReportsContentStartingAtZero()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.Headers.Range!.Ranges.Single().From.ShouldBe(3);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("hello"u8.ToArray()),
            };
        });
        using var client = new HttpClientRangeClient(new HttpClient(handler));

        await using var response = await client.GetRangeAsync("https://x/a", 3, CancellationToken.None);

        response.ContentStartByte.ShouldBe(0);
        using var reader = new StreamReader(response.Content);
        (await reader.ReadToEndAsync(TestContext.Current.CancellationToken)).ShouldBe("hello");
    }

    [Fact]
    public async Task GetRangeAsync_CompatiblePartialContent_ReportsRequestedStart()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("lo"u8.ToArray()),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 4, 5);
            return response;
        });
        using var client = new HttpClientRangeClient(new HttpClient(handler));

        await using var response = await client.GetRangeAsync("https://x/a", 3, CancellationToken.None);

        response.ContentStartByte.ShouldBe(3);
    }

    [Fact]
    public async Task GetRangeAsync_IncompatiblePartialContent_IsRejected()
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent("llo"u8.ToArray()),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, 5);
            return response;
        });
        using var client = new HttpClientRangeClient(new HttpClient(handler));

        await Should.ThrowAsync<ModelDownloadException>(() =>
            client.GetRangeAsync("https://x/a", 3, CancellationToken.None));
    }
}

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
