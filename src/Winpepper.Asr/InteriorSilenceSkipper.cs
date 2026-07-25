namespace Winpepper.Asr;

/// <summary>
/// Streaming analogue of Winpepper.Audio.SilenceTrimmer's INTERIOR-run policy.
/// Sits between the leading-silence gate and the mel extractor. Constants
/// duplicated from SilenceTrimmer (KeepMsPerEdge=600, abs floor=0.002, 20 ms
/// frames) because Winpepper.Asr does not reference Winpepper.Audio — same
/// precedent as ParakeetStreamingSession.LeadingSilenceRmsFloor.
/// Deviation from batch (inherent to streaming): fixed absolute threshold
/// instead of the utterance-adaptive one, so this trims LESS than batch, never
/// more. Runs at or below 2*keepEdge are kept whole (batch parity).
/// NOT used for the AssemblyAI realtime path: that endpoint expects continuous
/// mic audio with interior silence (vendor-side endpointing).
/// </summary>
public sealed class InteriorSilenceSkipper
{
    private readonly Action<ReadOnlyMemory<float>> _emit;
    private readonly double _rmsFloor;
    private readonly int _frameSamples;
    private readonly int _frameMs;
    private readonly int _keepFrames;

    // Trailing partial analysis frame held across pushes.
    private readonly float[] _partial;
    private int _partialCount;

    // Current silence run. _runBuffer holds COPIES of run frames not yet emitted
    // or dropped; once the run outgrows the keep budget the leading keepEdge is
    // emitted eagerly and the buffer becomes a rolling last-keepEdge window, so
    // memory stays bounded at ~2 * keepEdge worth of samples.
    private readonly List<float[]> _runBuffer = new();
    private int _runFrames;
    private bool _leadingEdgeEmitted;
    private int _skippedFrames;

    public InteriorSilenceSkipper(
        Action<ReadOnlyMemory<float>> emit,
        double rmsFloor = 0.002,
        int keepEdgeMs = 600,
        int sampleRate = 16000,
        int analysisFrameMs = 20)
    {
        _emit = emit;
        _rmsFloor = rmsFloor;
        _frameMs = analysisFrameMs;
        _frameSamples = sampleRate * analysisFrameMs / 1000; // 320 at defaults
        _keepFrames = keepEdgeMs / analysisFrameMs;          // 30 at defaults
        _partial = new float[_frameSamples];
    }

    /// <summary>Total interior/trailing silence dropped so far, in milliseconds.</summary>
    public int SkippedMs => _skippedFrames * _frameMs;

    /// <summary>Number of silence runs that had frames dropped.</summary>
    public int RunsSkipped { get; private set; }

    /// <summary>
    /// Classify the pushed samples into 20 ms analysis frames and buffer/emit
    /// them, strictly in original order. The output over a whole dictation is
    /// the sample-exact concatenation of the kept segments.
    /// </summary>
    public void Push(ReadOnlyMemory<float> samples)
    {
        var offset = 0;
        if (_partialCount > 0)
        {
            var take = Math.Min(_frameSamples - _partialCount, samples.Length);
            samples.Span.Slice(0, take).CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            offset = take;
            if (_partialCount < _frameSamples) return; // still incomplete
            _partialCount = 0;
            Classify(_partial.AsMemory(0, _frameSamples));
        }

        while (samples.Length - offset >= _frameSamples)
        {
            Classify(samples.Slice(offset, _frameSamples));
            offset += _frameSamples;
        }

        var rem = samples.Length - offset;
        if (rem > 0)
        {
            samples.Span.Slice(offset, rem).CopyTo(_partial);
            _partialCount = rem;
        }
    }

    /// <summary>
    /// End of dictation. Resolves the trailing silence run with the batch
    /// trimmer's single-speech-edge budget: a run at or below keepEdge is kept
    /// whole; a longer run keeps only its leading keepEdge (already emitted
    /// eagerly) and drops the rest. Any held partial analysis frame is emitted
    /// as-is (conservative: keep unclassified audio).
    /// </summary>
    public void Flush()
    {
        if (_runFrames > 0)
        {
            if (_runFrames <= _keepFrames)
            {
                foreach (var f in _runBuffer) _emit(f);
            }
            else
            {
                _skippedFrames += _runFrames - _keepFrames;
                RunsSkipped++;
            }
            ResetRun();
        }

        if (_partialCount > 0)
        {
            _emit(_partial.AsMemory(0, _partialCount));
            _partialCount = 0;
        }
    }

    private void Classify(ReadOnlyMemory<float> frame)
    {
        if (Rms(frame.Span) < _rmsFloor)
        {
            _runBuffer.Add(frame.ToArray()); // copy: caller may reuse the memory
            _runFrames++;
            if (!_leadingEdgeEmitted && _runFrames > _keepFrames)
            {
                // The leading keepEdge is kept under EVERY outcome, so emit it
                // as soon as the run outgrows it (bounded memory).
                for (var i = 0; i < _keepFrames; i++) _emit(_runBuffer[i]);
                _runBuffer.RemoveRange(0, _keepFrames);
                _leadingEdgeEmitted = true;
            }
            if (_leadingEdgeEmitted && _runBuffer.Count > _keepFrames)
                _runBuffer.RemoveRange(0, _runBuffer.Count - _keepFrames);
            return;
        }

        // Speech: resolve any pending silence run first, then emit the frame.
        if (_runFrames > 0)
        {
            // Run <= 2*keepEdge: everything still buffered is the un-emitted
            // remainder of the whole run (kept whole, SilenceTrimmer.cs:141-146).
            // Run > 2*keepEdge: the buffer is exactly the rolling trailing
            // keepEdge window; the middle was dropped (SilenceTrimmer.cs:134-140).
            foreach (var f in _runBuffer) _emit(f);
            if (_runFrames > 2 * _keepFrames)
            {
                _skippedFrames += _runFrames - 2 * _keepFrames;
                RunsSkipped++;
            }
            ResetRun();
        }
        _emit(frame);
    }

    private void ResetRun()
    {
        _runBuffer.Clear();
        _runFrames = 0;
        _leadingEdgeEmitted = false;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0.0;
        var sum = 0.0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }
}
