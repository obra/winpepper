using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI batch transcriber: encode float samples to WAV, upload, create a
/// transcript, then poll to completion on the caller's token (the cloud deadline
/// is owned by FallbackTranscriber). Waits a first-poll grace before poll #1,
/// treats unrecognized statuses explicitly, and (best-effort) deletes the remote
/// transcript after success. Never logs the API key.
/// </summary>
public sealed class AssemblyAiTranscriber : ITranscriber
{
    private readonly IAssemblyAiClient _client;
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiTranscriber> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<AssemblyAiRequestExtras> _extrasProvider;
    private readonly Action<Func<Task>> _scheduleDetached;

    public AssemblyAiTranscriber(
        IAssemblyAiClient client,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiTranscriber> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<AssemblyAiRequestExtras>? extrasProvider = null,
        Action<Func<Task>>? scheduleDetached = null)
    {
        _client = client;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
        _extrasProvider = extrasProvider ?? (() => AssemblyAiRequestExtras.Empty);
        _scheduleDetached = scheduleDetached ?? (a => _ = Task.Run(a));
    }

    public string ModelName => $"assemblyai/{_opts.Model}";

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);

        var sw = Stopwatch.StartNew();
        var wav = PcmWavEncoder.EncodeMono16k(mono16k.Span);
        var uploadUrl = await _client.UploadAsync(wav, ct);

        var extras = _extrasProvider();
        var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, extras, ct);
        _log.LogInformation("AssemblyAI transcript {Id} created ({Bytes} bytes uploaded)", id, wav.Length);

        var maxPolls = Math.Max(1, (int)Math.Ceiling(_opts.CloudDeadline / _opts.PollInterval));

        // First-poll grace: a freshly created clip needs ~750 ms to enter processing.
        await _delay(_opts.FirstPollDelay, ct);

        for (var i = 0; i < maxPolls; i++)
        {
            var tr = await _client.GetTranscriptAsync(id, ct);
            var status = (tr.Status ?? "").Trim();

            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("AssemblyAI transcript {Id} completed in {Ms}ms (confidence {Conf})",
                    id, sw.ElapsedMilliseconds, tr.Confidence);
                if (_opts.DeleteAfterTranscribe) ScheduleDelete(id);
                return new TranscriptionResult(tr.Text ?? "", ModelName);
            }
            if (status.Equals("error", StringComparison.OrdinalIgnoreCase))
                throw new AssemblyAiException($"AssemblyAI transcription failed: {tr.Error}");

            if (!status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("processing", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown status: never silently drop a possible completion — log and keep polling.
                _log.LogWarning("AssemblyAI transcript {Id} returned unrecognized status '{Status}'; continuing to poll",
                    id, tr.Status);
            }

            if (i < maxPolls - 1) await _delay(_opts.PollInterval, ct);
        }

        throw new AssemblyAiException($"AssemblyAI transcription timed out after {maxPolls} polls.");
    }

    private void ScheduleDelete(string id)
        => _scheduleDetached(async () =>
        {
            try { await _client.DeleteTranscriptAsync(id, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "AssemblyAI transcript {Id} delete failed (non-fatal)", id); }
        });
}
