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
    private readonly RunningMelNormalizer _normalizer;
    private readonly List<double[]> _pending = new(); // log-mel frames not yet encoded
    private readonly List<double[]> _context = new(); // trailing already-encoded frames
    private readonly TdtDecoderState _state;
    private readonly List<int> _tokens = new();
    private readonly List<int> _frameIndices = new();
    private readonly List<int> _durations = new();
    private int _globalEncFrames;
    private int? _subsamplingFactor; // derived from the first encode's actual output
    private bool _speechSeen;        // leading-silence gate latch
    private bool _streamed;
    private bool _corrupt;

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
            _mel.Push(mono16k.Span);
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
            return await Task.Run(() =>
            {
                _mel.Finish();
                _mel.Drain(_pending);
                if (_pending.Count > 0)
                {
                    var tail = new List<double[]>(_pending);
                    _pending.Clear();
                    EncodeAndDecode(tail);
                }
                return new TranscriptionResult(_backend.DecodeTokens(_tokens), _modelName);
            }, ct);
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
        _subsamplingFactor ??= (withContext.Count - 1) / Math.Max(1, enc.Frames - 1);
        var factor = _subsamplingFactor.Value;
        if ((withContext.Count - 1) / factor + 1 != enc.Frames)
            throw new InvalidOperationException(
                $"encoder output length {enc.Frames} != floor((T-1)/{factor})+1 for T={withContext.Count}");
        var discard = _context.Count == 0 ? 0 : (_context.Count - 1) / factor + 1;

        TdtGreedyDecoder.Decode(_backend, enc, _state, _tokens, _frameIndices, _durations,
            startFrame: discard, frameIndexOffset: _globalEncFrames - discard);
        _globalEncFrames += enc.Frames - discard;
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
