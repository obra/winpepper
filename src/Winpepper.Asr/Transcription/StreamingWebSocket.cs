using System.Net.WebSockets;
using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>Thin seam over ClientWebSocket so AssemblyAiStreamingSession is testable.</summary>
public interface IStreamingWebSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct);
    Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct);
    Task SendTextAsync(string json, CancellationToken ct);

    /// <summary>Next complete text message, or null when the server closed the socket.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
}

/// <summary>Real WebSocket. Network-facing; exercised by the latency benchmark's
/// real-remote scenario and by production — unit tests use the fake.</summary>
public sealed class ClientStreamingWebSocket : IStreamingWebSocket
{
    private readonly ClientWebSocket _ws = new();

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", apiKey); // raw key — no Bearer prefix
        return _ws.ConnectAsync(uri, ct);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        => await _ws.SendAsync(audio, WebSocketMessageType.Binary, endOfMessage: true, ct);

    public async Task SendTextAsync(string json, CancellationToken ct)
        => await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
        }
        catch { /* best-effort close */ }
        _ws.Dispose();
    }
}
