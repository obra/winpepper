using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>A single AssemblyAI batch-transcript status snapshot.</summary>
public sealed record AssemblyAiTranscript(string Status, string? Text, double? Confidence, double? AudioDuration, string? Error);

public interface IAssemblyAiClient
{
    Task<string> UploadAsync(byte[] audio, CancellationToken ct);
    Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct);
    Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct);
    Task<bool> ValidateKeyAsync(CancellationToken ct);
}

/// <summary>
/// Raw-HttpClient AssemblyAI batch client. There is no maintained official C#
/// SDK, so every call is hand-built. Retry policy: transient 5xx/429/network
/// errors are retried with backoff (429 honors Retry-After); 401/400/404 are
/// terminal. The create-transcript POST is only retried before an id exists.
/// </summary>
public sealed class AssemblyAiClient : IAssemblyAiClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiClient> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _rng = new();

    public AssemblyAiClient(
        HttpClient http,
        Func<string?> apiKeyProvider,
        AssemblyAiOptions options,
        ILogger<AssemblyAiClient> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _apiKey = apiKeyProvider;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public async Task<string> UploadAsync(byte[] audio, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/upload");
            var content = new ByteArrayContent(audio);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content = content;
            return req;
        }, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ReadString(json, "upload_url");
    }

    public async Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct)
    {
        var payload = new
        {
            audio_url = audioUrl,
            speech_models = new[] { model }, // plural array — singular `speech_model` is deprecated
            format_text = true,
            punctuate = true,
            disfluencies = false,
            language_code = _opts.LanguageCode,
        };
        var body = JsonSerializer.Serialize(payload);

        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/transcript")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return req;
        }, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ReadString(json, "id");
    }

    public async Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/{id}"), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AssemblyAiTranscript(
            Status: root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "",
            Text: root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            Confidence: root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
            AudioDuration: root.TryGetProperty("audio_duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
            Error: root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }

    public async Task<bool> ValidateKeyAsync(CancellationToken ct)
    {
        // GET a bogus id: 401 => bad key; anything else (typically 404) => key accepted.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/winpepper-key-check-000000000000");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        return (int)resp.StatusCode != 401;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        var key = _apiKey();
        if (string.IsNullOrEmpty(key))
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);
        req.Headers.TryAddWithoutValidation("authorization", key); // NO "Bearer " prefix
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var req = requestFactory();
            AddAuth(req);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt <= _opts.MaxTransientRetries)
            {
                await _delay(Backoff(attempt), ct);
                continue;
            }

            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return resp;

            if (code == 401)
            {
                resp.Dispose();
                throw new AssemblyAiException("AssemblyAI rejected the API key (401). Check your key.", 401, isAuthError: true);
            }
            if (code is 400 or 404)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();
                throw new AssemblyAiException($"AssemblyAI request failed ({code}): {body}", code);
            }
            if (code == 429)
            {
                var wait = RetryAfter(resp) ?? Backoff(attempt);
                resp.Dispose();
                if (attempt > _opts.MaxTransientRetries)
                    throw new AssemblyAiException("AssemblyAI rate limit (429) exceeded retries.", 429);
                await _delay(wait, ct);
                continue;
            }
            if (code is 500 or 502 or 503 or 504)
            {
                resp.Dispose();
                if (attempt > _opts.MaxTransientRetries)
                    throw new AssemblyAiException($"AssemblyAI server error ({code}) exceeded retries.", code);
                await _delay(Backoff(attempt), ct);
                continue;
            }

            var other = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new AssemblyAiException($"AssemblyAI unexpected status ({code}): {other}", code);
        }
    }

    private TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 250 + _rng.Next(0, 250)); // exponential + jitter

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        TimeSpan? raw = null;
        if (resp.Headers.RetryAfter?.Delta is { } delta) raw = delta;
        else if (resp.Headers.TryGetValues("Retry-After", out var values)
                 && int.TryParse(values.FirstOrDefault(), out var seconds))
            raw = TimeSpan.FromSeconds(seconds);

        if (raw is null) return null;
        // Clamp defensively: a negative value would throw from Task.Delay, and a
        // huge value would freeze dictation past any sane budget.
        var clamped = raw.Value;
        if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
        if (clamped > MaxRetryAfter) clamped = MaxRetryAfter;
        return clamped;
    }

    private static string ReadString(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new AssemblyAiException($"AssemblyAI response missing '{property}'.");
    }
}
