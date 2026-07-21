using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiClientTests
{
    private static AssemblyAiClient Make(FakeHttpMessageHandler handler, List<TimeSpan> delays, string? key = "KEY")
    {
        var http = new HttpClient(handler);
        var opts = new AssemblyAiOptions { MaxTransientRetries = 3 };
        return new AssemblyAiClient(http, () => key, opts, NullLogger<AssemblyAiClient>.Instance,
            (ts, _) => { delays.Add(ts); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Upload_SendsRawBytesWithBareAuthHeader()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/xyz\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var body = new byte[] { 1, 2, 3, 4 };
        var url = await client.UploadAsync(body, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/xyz");
        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Post);
        req.RequestUri!.ToString().ShouldEndWith("/v2/upload");
        req.Headers.GetValues("authorization").ShouldContain("KEY"); // no "Bearer "
        req.Content!.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        handler.RequestBodies[0].ShouldBe(body); // raw bytes, not JSON/multipart
    }

    [Fact]
    public async Task Upload_401_ThrowsAuthErrorWithoutRetry()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => client.UploadAsync(new byte[] { 1 }, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
        ex.StatusCode.ShouldBe(401);
        handler.Requests.Count.ShouldBe(1);
        delays.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Upload_400_ThrowsWithoutRetry()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => client.UploadAsync(new byte[] { 1 }, CancellationToken.None));
        ex.StatusCode.ShouldBe(400);
        ex.IsAuthError.ShouldBeFalse();
        handler.Requests.Count.ShouldBe(1);
        delays.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Upload_429_HonorsRetryAfterThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}", mutate: r => r.Headers.TryAddWithoutValidation("Retry-After", "2"))
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1);
        delays[0].ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Upload_503_BacksOffThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1);
        delays[0].ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CreateTranscript_SendsSpeechModelPayload_ReturnsId()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"t-123\",\"status\":\"queued\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var id = await client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-2", CancellationToken.None);

        id.ShouldBe("t-123");
        var json = Encoding.UTF8.GetString(handler.RequestBodies[0]);
        json.ShouldContain("\"speech_models\":[\"universal-2\"]"); // plural array (singular is deprecated)
        json.ShouldContain("\"audio_url\":\"https://cdn/aai/ok\"");
        json.ShouldContain("\"format_text\":true");
        json.ShouldContain("\"punctuate\":true");
        json.ShouldContain("\"disfluencies\":false");
        json.ShouldContain("\"language_code\":\"en_us\"");
    }

    [Fact]
    public async Task GetTranscript_ParsesCompletedFields()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"completed\",\"text\":\"hello world\",\"confidence\":0.97,\"audio_duration\":3.2}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var tr = await client.GetTranscriptAsync("t-123", CancellationToken.None);

        tr.Status.ShouldBe("completed");
        tr.Text.ShouldBe("hello world");
        tr.Confidence.ShouldBe(0.97);
        tr.AudioDuration.ShouldBe(3.2);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().ShouldEndWith("/v2/transcript/t-123");
    }

    [Fact]
    public async Task ValidateKey_404MeansValid_401MeansBadKey()
    {
        var goodHandler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        var badHandler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var delays = new List<TimeSpan>();

        (await Make(goodHandler, delays).ValidateKeyAsync(CancellationToken.None)).ShouldBeTrue();
        (await Make(badHandler, delays).ValidateKeyAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("-5", 0)]        // negative -> clamped to 0
    [InlineData("99999", 30)]    // huge -> clamped to 30
    [InlineData("banana", null)] // non-numeric -> ignored, falls back to backoff (>0)
    public async Task Upload_429_ClampsGarbageRetryAfter(string headerValue, int? expectedSeconds)
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}", mutate: r => r.Headers.TryAddWithoutValidation("Retry-After", headerValue))
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        delays.Count.ShouldBe(1);
        delays[0].ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        delays[0].ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        if (expectedSeconds is int s)
            delays[0].ShouldBe(TimeSpan.FromSeconds(s));
        else
            delays[0].ShouldBeGreaterThan(TimeSpan.Zero); // non-numeric -> backoff jitter
    }
}
