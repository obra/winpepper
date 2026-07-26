using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Streaming local transcription. Log-mel frames are computed incrementally as
/// audio arrives; every chunkMelFrames of NEW frames the encoder runs over
/// [left context + chunk] (running-stats normalization), the context's encoder
/// frames are discarded, and the greedy TDT decoder consumes the rest with its
/// state carried across chunks. At stop time only the tail remains, so post-stop
/// latency ≈ cost(tail) instead of cost(whole recording).
///
/// Deliberate deviations from batch (inherent to streaming ASR): running-stats
/// normalization instead of whole-utterance stats, and limited left / no right
/// encoder context. Dictations shorter than one chunk never stream — FinishAsync
/// takes the exact batch path via <c>batchFallback</c>, and ANY streaming failure
/// also lands on <c>batchFallback(fullAudio)</c>, so reliability never regresses.
///
/// KNOWN DEFECT (real int8 Parakeet-TDT model, validated 2026-07-25): chunked
/// decodes collapse to blanks after the first encode — the joint argmaxes blank
/// with large duration skips on every post-first encode, deterministically, on
/// both DirectML and CPU EPs, regardless of normalization strategy, decoder
/// state handling, or chunk/context sizes (Task 7b experiments; even ideal
/// batch-preprocessed 3 s MID-utterance windows can decode to zero tokens, so
/// this is a model-level limitation of short-window chunked inference, not a
/// plumbing bug). No exception is thrown, so the corrupt path alone cannot see
/// it. The guard below makes that failure LOUD instead of silently truncating:
/// any post-first encode that decodes to zero tokens — or a streamed dictation
/// whose total decode is empty — forces FinishAsync onto the batch fallback.
/// The trade-off (a genuinely silent chunk also forfeits the latency win) only
/// ever costs speed, never words.
/// </summary>
public sealed class ParakeetStreamingSession : IStreamingTranscriptionSession
{
    private readonly IParakeetBackend _backend;
    private readonly string _modelName;
    private readonly PreprocessorConfig _config;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> _batchFallback;
    private readonly int _chunkMelFrames;
    private readonly int _leftContextMelFrames;
    private readonly ILogger? _log;

    private readonly StreamingLogMelExtractor _mel;
    private readonly InteriorSilenceSkipper _skipper;
    private readonly RunningMelNormalizer _normalizer;
    private readonly List<double[]> _pending = new(); // log-mel frames not yet encoded
    private readonly List<double[]> _context = new(); // trailing already-encoded frames
    private readonly TdtDecoderState _state;
    private readonly List<int> _tokens = new();
    private int _globalEncFrames;
    private int? _subsamplingFactor; // derived from the first encode's actual output
    private bool _speechSeen;        // leading-silence gate latch
    private bool _streamed;
    private bool _corrupt;
    private bool _blankCollapse;     // a post-first encode decoded to zero tokens (known int8 defect)

    /// <summary>
    /// Frame-RMS floor for the leading-silence gate. Mirrors the batch trimmer's
    /// absolute silence floor (Winpepper.Audio.SilenceTrimmer.ThresholdAbsFloor,
    /// 0.002 — duplicated here because Winpepper.Asr does not reference
    /// Winpepper.Audio). The batch path trims silence before ASR
    /// (PipelineHost.TrimForTranscription → SilenceTrimmer.Trim); the streaming
    /// path must gate leading silence too, because Parakeet-TDT deterministically
    /// deletes tokens around silence (NeMo-Speech #15757; FluidAudio #746) and
    /// the 500 ms pre-roll is mostly silence.
    /// </summary>
    private const double LeadingSilenceRmsFloor = 0.002;

    public ParakeetStreamingSession(
        IParakeetBackend backend,
        string modelName,
        PreprocessorConfig config,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback,
        int chunkMelFrames = 200,        // 2 s of audio (100 mel frames/s at hop 160) — small
                                         // chunks keep the post-stop TAIL small; the extra
                                         // context re-encoding all happens during recording
        int leftContextMelFrames = 100,  // 1 s of context re-encoded per chunk
        ILogger? log = null)
    {
        _backend = backend;
        _modelName = modelName;
        _config = config;
        _batchFallback = batchFallback;
        _chunkMelFrames = chunkMelFrames;
        _leftContextMelFrames = leftContextMelFrames;
        _log = log;
        _mel = new StreamingLogMelExtractor(config);
        _skipper = new InteriorSilenceSkipper(m => _mel.Push(m.Span));
        _normalizer = new RunningMelNormalizer(config.FeatureSize);
        _state = new TdtDecoderState(backend.DecoderHiddenLayers, backend.DecoderHiddenDim, backend.BlankId);
    }

    public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (_corrupt) return ValueTask.CompletedTask;
        if (!_speechSeen)
        {
            // Leading-silence gate: skip whole pushed frames (never fed to the
            // mel extractor — they would also pollute the running normalizer's
            // stats) until the first frame with speech-level energy.
            if (Rms(mono16k.Span) < LeadingSilenceRmsFloor) return ValueTask.CompletedTask;
            _speechSeen = true;
        }
        try
        {
            // Interior-silence gate: the skipper drops long post-onset silence
            // runs BEFORE the mel extractor. StreamingLogMelExtractor is exact
            // over the same total audio regardless of chunking, so this is
            // indistinguishable from batch-transcribing a shorter trimmed
            // buffer; the chunk/frame math below is untouched.
            _skipper.Push(mono16k);
            _mel.Drain(_pending);
            while (_pending.Count >= _chunkMelFrames)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = _pending.GetRange(0, _chunkMelFrames);
                _pending.RemoveRange(0, _chunkMelFrames);
                EncodeAndDecode(chunk);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _corrupt = true;
            _log?.LogWarning(ex, "streaming local ASR failed mid-dictation; will batch-transcribe at stop");
        }
        return ValueTask.CompletedTask;
    }

    public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        if (_corrupt || !_streamed)
            return await _batchFallback(fullAudio, ct);
        try
        {
            var streamed = await Task.Run<TranscriptionResult?>(() =>
            {
                _skipper.Flush();
                _mel.Finish();
                _mel.Drain(_pending);
                if (_pending.Count > 0)
                {
                    var tail = new List<double[]>(_pending);
                    _pending.Clear();
                    EncodeAndDecode(tail);
                }
                // Blank-collapse guard (see class doc): a zero-token post-first
                // encode, or an entirely empty streamed decode, means the stream
                // cannot be trusted — hand the decision back to the batch path.
                if (_blankCollapse || _tokens.Count == 0)
                    return null;
                var result = new TranscriptionResult(_backend.DecodeTokens(_tokens), _modelName);
                if (_skipper.SkippedMs > 0)
                    _log?.LogInformation(
                        "streaming interior silence skipped: {Ms} ms across {Runs} runs",
                        _skipper.SkippedMs, _skipper.RunsSkipped);
                return result;
            }, ct);
            if (streamed is not null) return streamed;
            _log?.LogWarning(
                "streaming decode collapsed to blanks (known int8 chunked-decode defect); " +
                "batch-transcribing the full buffer instead of returning a truncated transcript");
            return await _batchFallback(fullAudio, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "streaming local ASR failed at stop; batch-transcribing the full buffer");
            return await _batchFallback(fullAudio, ct);
        }
    }

    private void EncodeAndDecode(List<double[]> chunk)
    {
        _normalizer.Add(chunk);
        var withContext = new List<double[]>(_context.Count + chunk.Count);
        withContext.AddRange(_context);
        withContext.AddRange(chunk);

        var features = _normalizer.Normalize(withContext); // [ctx+chunk, FeatureSize]
        var enc = _backend.Encode(features);

        // Discard the context's encoder frames using the encoder's EXACT
        // output-length function: for input length T this export family produces
        // floor((T-1)/F) + 1 frames (F = subsampling factor; 8 for Parakeet-TDT,
        // per onnx-asr's nemo.py). F is derived once from the first encode's
        // actual output — never hardcoded — and re-asserted on every encode.
        // A proportional Math.Round diverges at banker's-rounding midpoints
        // (e.g. ctx=100, tail=4: round(12.5) = 12 vs the exact 13), which would
        // double-decode a boundary frame; the exact form eliminates the class.
        // Guard: a single-frame first encode gives no baseline to derive F from
        // (the derivation degenerates to T-1). Unreachable with the default
        // 200-mel-frame chunks (>= 25 output frames), but if it ever happens,
        // fall back to the known Parakeet-TDT factor of 8 — the assertion below
        // still validates it against this and every subsequent encode.
        _subsamplingFactor ??= enc.Frames > 1
            ? (withContext.Count - 1) / (enc.Frames - 1)
            : 8;
        var factor = _subsamplingFactor.Value;
        if ((withContext.Count - 1) / factor + 1 != enc.Frames)
            throw new InvalidOperationException(
                $"encoder output length {enc.Frames} != floor((T-1)/{factor})+1 for T={withContext.Count}");
        var discard = _context.Count == 0 ? 0 : (_context.Count - 1) / factor + 1;

        // Token timings are unused on the streaming path (only the token ids feed
        // DecodeTokens at finish), so hand the decoder throwaway lists instead of
        // accumulating them for the whole dictation.
        var tokensBefore = _tokens.Count;
        TdtGreedyDecoder.Decode(_backend, enc, _state, _tokens, new List<int>(), new List<int>(),
            startFrame: discard, frameIndexOffset: _globalEncFrames - discard);
        _globalEncFrames += enc.Frames - discard;

        // Blank-collapse guard (see class doc): a post-first encode that decodes
        // to ZERO tokens matches the known int8 chunked-decode defect — the
        // stream can no longer be trusted, so FinishAsync must take the batch
        // fallback. A genuinely silent chunk trips this too; that false positive
        // costs only the latency win, never transcript content.
        if (_streamed && _tokens.Count == tokensBefore && !_blankCollapse)
        {
            _blankCollapse = true;
            _log?.LogWarning(
                "streaming encode decoded to zero tokens (known int8 chunked-decode defect); " +
                "will batch-transcribe the full buffer at stop");
        }
        _streamed = true;

        _context.AddRange(chunk);
        if (_context.Count > _leftContextMelFrames)
            _context.RemoveRange(0, _context.Count - _leftContextMelFrames);
        if (_leftContextMelFrames == 0) _context.Clear();
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0.0;
        var sum = 0.0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
