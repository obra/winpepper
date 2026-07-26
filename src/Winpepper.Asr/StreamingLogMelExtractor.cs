namespace Winpepper.Asr;

/// <summary>
/// Incremental log-mel extractor producing frames EXACTLY equal (pre-normalization)
/// to MelFeatureExtractor's steps 1–2 over the same total audio, regardless of how
/// the audio is chunked. Frame t is centered at sample t*Hop and needs samples up
/// to t*Hop + NFft/2 (exclusive); mid-stream only frames whose full right context
/// has arrived are emitted, and Finish() zero-pads the tail exactly like the batch
/// path so the total frame count matches batch (totalSamples/Hop + 1).
/// </summary>
public sealed class StreamingLogMelExtractor
{
    private readonly PreprocessorConfig _config;
    private readonly double[] _window;
    private readonly double[][] _melFilters;
    private readonly List<float> _raw = new(); // unconsumed raw samples; _raw[0] is global index _rawStart
    private long _rawStart;
    private long _totalSamples;
    private long _nextFrame;
    private bool _finished;

    public StreamingLogMelExtractor(PreprocessorConfig config)
    {
        _config = config;
        _window = MelFeatureExtractor.BuildHannWindow(config.NFft, config.WinLength);
        _melFilters = MelFeatureExtractor.BuildSlaneyMelFilters(config.NFft, config.FeatureSize, config.SamplingRate);
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        if (_finished) throw new InvalidOperationException("Push after Finish");
        foreach (var s in samples) _raw.Add(s);
        _totalSamples += samples.Length;
    }

    /// <summary>After Finish(), Drain emits the zero-right-padded tail frames.</summary>
    public void Finish() => _finished = true;

    /// <summary>Append every frame computable so far (double[FeatureSize] each) to <paramref name="sink"/>.</summary>
    public void Drain(List<double[]> sink)
    {
        var pad = _config.NFft / 2;
        while (true)
        {
            var frameStart = _nextFrame * _config.HopLength - pad; // global; < 0 near utterance start
            var frameEnd = frameStart + _config.NFft;              // exclusive
            if (!_finished && frameEnd > _totalSamples) return;    // needs future audio
            if (_finished && _nextFrame > _totalSamples / _config.HopLength) return; // batch: len/hop + 1 frames
            sink.Add(ComputeFrame(frameStart));
            _nextFrame++;
            TrimConsumed(pad);
        }
    }

    private double[] ComputeFrame(long frameStart)
    {
        var n = _config.NFft;
        var frame = new double[n];
        for (var k = 0; k < n; k++)
            frame[k] = Preemphasized(frameStart + k) * _window[k];

        var nBins = n / 2 + 1;
        var power = new double[nBins];
        MelFeatureExtractor.HandRolledRfftPower(frame, n, power);

        var mel = new double[_config.FeatureSize];
        for (var m = 0; m < _config.FeatureSize; m++)
        {
            double acc = 0.0;
            var filter = _melFilters[m];
            for (var k = 0; k < nBins; k++) acc += power[k] * filter[k];
            mel[m] = Math.Log(Math.Max(acc + MelFeatureExtractor.MelOffset, MelFeatureExtractor.MelMin));
        }
        return mel;
    }

    // Batch preemphasis is x[j] -= p * x[j-1] for j >= 1 with x[0] unchanged, and
    // the NFft/2 zero padding is added AFTER preemphasis — so out-of-range indices
    // are exact zeros and in-range values depend on at most one previous raw sample.
    private double Preemphasized(long g)
    {
        if (g < 0 || g >= _totalSamples) return 0.0;
        var raw = (double)RawAt(g);
        return g == 0 ? raw : raw - _config.Preemphasis * RawAt(g - 1);
    }

    private float RawAt(long g) => _raw[(int)(g - _rawStart)];

    private void TrimConsumed(int pad)
    {
        // Keep everything the NEXT frame (and its preemphasis lookback) needs.
        var keepFrom = _nextFrame * _config.HopLength - pad - 1;
        if (keepFrom <= _rawStart) return;
        var drop = (int)Math.Min(keepFrom - _rawStart, _raw.Count);
        if (drop > 0) { _raw.RemoveRange(0, drop); _rawStart += drop; }
    }
}
