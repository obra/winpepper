using System.Net;

namespace Winpepper.Asr.Tests;

/// <summary>Queues scripted responses/throws and records every request for assertions.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<byte[]> RequestBodies { get; } = new();

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body, string contentType = "application/json",
        Action<HttpResponseMessage>? mutate = null)
    {
        _responses.Enqueue(_ =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
            };
            mutate?.Invoke(resp);
            return resp;
        });
        return this;
    }

    /// <summary>Enqueue a send that throws (e.g. a per-request timeout as TaskCanceledException).</summary>
    public FakeHttpMessageHandler EnqueueThrow(Exception ex)
    {
        _responses.Enqueue(_ => throw ex);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken));
        // Honor the (possibly linked) per-request token so a triggered timeout surfaces as cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        if (_responses.Count == 0) throw new InvalidOperationException("No scripted response left.");
        return _responses.Dequeue()(request);
    }
}
