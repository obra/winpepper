using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI batch transcriber: encode float samples to WAV, upload raw bytes,
/// create a transcript, then poll to completion. Enforces a total wall-clock cap
/// (via a linked CTS) and a deterministic poll budget (ceil(TotalTimeout/PollInterval)).
/// </summary>
public sealed class AssemblyAiTranscriber : ITranscriber
{
    private readonly IAssemblyAiClient _client;
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiTranscriber> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AssemblyAiTranscriber(
        IAssemblyAiClient client,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiTranscriber> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public string ModelName => $"assemblyai/{_opts.Model}";

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opts.TotalTimeout);
        var token = cts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            var wav = PcmWavEncoder.EncodeMono16k(mono16k.Span);
            var uploadUrl = await _client.UploadAsync(wav, token);
            var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, AssemblyAiRequestExtras.Empty, token);
            _log.LogInformation("AssemblyAI transcript {Id} created ({Bytes} bytes uploaded)", id, wav.Length);

            var maxPolls = Math.Max(1, (int)Math.Ceiling(_opts.TotalTimeout / _opts.PollInterval));
            for (var i = 0; i < maxPolls; i++)
            {
                var tr = await _client.GetTranscriptAsync(id, token);
                if (tr.Status == "completed")
                {
                    _log.LogInformation("AssemblyAI transcript {Id} completed in {Ms}ms (confidence {Conf})",
                        id, sw.ElapsedMilliseconds, tr.Confidence);
                    return new TranscriptionResult(tr.Text ?? "", ModelName);
                }
                if (tr.Status == "error")
                    throw new AssemblyAiException($"AssemblyAI transcription failed: {tr.Error}");

                await _delay(_opts.PollInterval, token);
            }

            throw new AssemblyAiException($"AssemblyAI transcription timed out after {_opts.TotalTimeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked CTS fired the wall-clock cap, not the caller's token.
            throw new AssemblyAiException($"AssemblyAI transcription timed out after {_opts.TotalTimeout.TotalSeconds:0}s.");
        }
    }
}
