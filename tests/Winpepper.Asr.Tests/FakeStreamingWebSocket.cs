using System.Threading.Channels;
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Scripted WebSocket double: records sends, replays queued server messages.</summary>
public sealed class FakeStreamingWebSocket : IStreamingWebSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public Uri? ConnectedUri { get; private set; }
    public string? ApiKey { get; private set; }
    public List<byte[]> BinaryFrames { get; } = new();
    public List<string> TextFrames { get; } = new();
    public Exception? ThrowOnConnect { get; set; }
    public Exception? ThrowOnSendBinary { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>When true (default), a Terminate send auto-queues the server's termination reply.</summary>
    public bool AutoTerminate { get; set; } = true;

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        if (ThrowOnConnect is not null) throw ThrowOnConnect;
        ConnectedUri = uri;
        ApiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        if (ThrowOnSendBinary is not null) throw ThrowOnSendBinary;
        BinaryFrames.Add(audio.ToArray());
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string json, CancellationToken ct)
    {
        TextFrames.Add(json);
        if (AutoTerminate && json.Contains("Terminate"))
            _incoming.Writer.TryWrite("{\"type\":\"Termination\",\"audio_duration_seconds\":1}");
        return Task.CompletedTask;
    }

    public void EnqueueServerMessage(string json) => _incoming.Writer.TryWrite(json);
    public void CloseFromServer() => _incoming.Writer.TryWrite(null);

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        => await _incoming.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryWrite(null); // unblock a pending receive
        return ValueTask.CompletedTask;
    }
}
