using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI Universal-Streaming (v3 WebSocket) transcriber. Audio streams as
/// raw PCM16LE binary messages while the user is still speaking; on stop a
/// Terminate message flushes the session and the final transcript is assembled
/// from the latest transcript per turn_order. A session that finishes with ZERO
/// pushed samples delegates to <paramref name="batchFallback"/> (the cloud REST
/// batch path) instead of bursting the buffer over the socket — the server
/// throttles ingest to ~1.25x realtime and errors (3007) past a buffered
/// backlog, so bursting is slower than REST and not completeness-guaranteed.
/// Never logs the API key. FallbackStreamingTranscriber (Task 8) owns
/// retries/local fallback — failures here throw AssemblyAiException.
/// </summary>
public sealed class AssemblyAiStreamingTranscriber : IStreamingTranscriber
{
    private readonly Func<IStreamingWebSocket> _socketFactory;
    private readonly ITranscriber _batchFallback; // cloud REST batch (AssemblyAiTranscriber)
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger _log;

    public AssemblyAiStreamingTranscriber(
        Func<IStreamingWebSocket> socketFactory,
        ITranscriber batchFallback,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiStreamingTranscriber> logger)
    {
        _socketFactory = socketFactory;
        _batchFallback = batchFallback;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
    }

    public string ModelName => "assemblyai/universal-streaming";

    public async Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);
        var key = _keyStore.Load()
            ?? throw new AssemblyAiException("AssemblyAI API key unreadable.", isAuthError: true);

        var socket = _socketFactory();
        var uri = new Uri($"{_opts.StreamingBaseUrl}/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true");
        await socket.ConnectAsync(uri, key, ct);
        _log.LogInformation("AssemblyAI streaming session connected");
        return new AssemblyAiStreamingSession(
            socket, ModelName,
            (audio, ct2) => _batchFallback.TranscribeAsync(audio, ct2),
            _log);
    }
}

public sealed class AssemblyAiStreamingSession : IStreamingTranscriptionSession
{
    private const int MinSendSamples = 800;      // 50 ms at 16 kHz — the API's minimum message

    private readonly IStreamingWebSocket _socket;
    private readonly string _modelName;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> _batchFallback;
    private readonly ILogger _log;
    private readonly Task _receiveLoop;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly object _turnLock = new();
    private readonly SortedDictionary<int, string> _turns = new(); // turn_order → latest transcript
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<float> _sendBuffer = new();
    private long _pushedSamples;
    private bool _sawTermination; // written only by the receive loop
    private volatile Exception? _serverError;

    public AssemblyAiStreamingSession(
        IStreamingWebSocket socket,
        string modelName,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback,
        ILogger log)
    {
        _socket = socket;
        _modelName = modelName;
        _batchFallback = batchFallback;
        _log = log;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var json = await _socket.ReceiveTextAsync(_loopCts.Token);
                if (json is null)
                {
                    // Socket closed. Without a prior Termination (or Error) this
                    // is an ABNORMAL close — surface it so the fallback wrapper
                    // engages instead of returning a truncated transcript.
                    if (!_sawTermination && _serverError is null)
                        _serverError = new AssemblyAiException(
                            "AssemblyAI streaming connection closed unexpectedly.");
                    return;
                }
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "Turn":
                    {
                        var order = root.TryGetProperty("turn_order", out var o) ? o.GetInt32() : 0;
                        var transcript = root.TryGetProperty("transcript", out var tr)
                            ? tr.GetString() ?? "" : "";
                        lock (_turnLock) _turns[order] = transcript;
                        break;
                    }
                    case "Termination":
                    case "SessionTerminated": // legacy name, tolerated defensively
                        _sawTermination = true;
                        return;
                    case "Error":
                    {
                        var msg = root.TryGetProperty("error", out var e) ? e.GetString() : json;
                        _serverError = new AssemblyAiException($"AssemblyAI streaming error: {msg}");
                        return;
                    }
                    default:
                        break; // Begin & friends
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _serverError = ex is AssemblyAiException ? ex
                : new AssemblyAiException("AssemblyAI streaming receive failed.", inner: ex);
        }
        finally
        {
            _terminated.TrySetResult();
        }
    }

    public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (_serverError is not null) return ValueTask.FromException(_serverError);
        BufferSamples(mono16k.Span);
        _pushedSamples += mono16k.Length;
        if (_sendBuffer.Count < MinSendSamples) return ValueTask.CompletedTask;
        var chunk = _sendBuffer.ToArray();
        _sendBuffer.Clear();
        return new ValueTask(_socket.SendBinaryAsync(Pcm16.FromFloats(chunk), ct));
    }

    private void BufferSamples(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) _sendBuffer.Add(s);
    }

    public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        ThrowIfFailed();

        if (_pushedSamples == 0)
        {
            // Session materialized only at stop time (nothing streamed). Bursting
            // the whole buffer over the socket is NOT batch-equivalent: the server
            // throttles ingest to ~1.25x realtime and errors (3007) past a
            // buffered backlog, so it is both slower than REST and not
            // completeness-guaranteed. Delegate to the cloud batch REST path —
            // behavior identical to today for late-materialized sessions.
            return await _batchFallback(fullAudio, ct);
        }

        if (_sendBuffer.Count > 0)
        {
            // Terminate's tail flush of the <=1 s in-flight remainder IS
            // documented — only this residual is sent here.
            var tail = _sendBuffer.ToArray();
            _sendBuffer.Clear();
            await SendPadded(tail, ct);
        }

        await _socket.SendTextAsync("{\"type\":\"Terminate\"}", ct);
        await _terminated.Task.WaitAsync(ct);
        ThrowIfFailed();

        string text;
        int turnCount;
        lock (_turnLock)
        {
            turnCount = _turns.Count;
            text = string.Join(" ", _turns.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        _log.LogInformation("AssemblyAI streaming session finished ({Turns} turns)", turnCount);
        return new TranscriptionResult(text.Trim(), _modelName);
    }

    private Task SendPadded(ReadOnlyMemory<float> samples, CancellationToken ct)
    {
        // Messages under 50 ms are rejected; zero-pad the final sliver.
        if (samples.Length >= MinSendSamples)
            return _socket.SendBinaryAsync(Pcm16.FromFloats(samples.Span), ct);
        var padded = new float[MinSendSamples];
        samples.Span.CopyTo(padded);
        return _socket.SendBinaryAsync(Pcm16.FromFloats(padded), ct);
    }

    private void ThrowIfFailed()
    {
        if (_serverError is not null) throw _serverError;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try { await _socket.DisposeAsync(); } catch { /* best-effort */ }
        try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _loopCts.Dispose();
    }
}
