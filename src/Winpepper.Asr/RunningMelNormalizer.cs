namespace Winpepper.Asr;

/// <summary>
/// Streaming replacement for MelFeatureExtractor's per-utterance normalization
/// (step 3). Batch normalization needs the WHOLE utterance's mean/std, which a
/// streaming path cannot have; this uses running statistics over every log-mel
/// frame seen so far (same ddof=1 convention and epsilon). When all frames are
/// Add()ed before the first Normalize call the output equals batch (up to
/// one-pass vs two-pass variance rounding).
/// </summary>
public sealed class RunningMelNormalizer
{
    private const double Epsilon = 1e-5; // matches MelFeatureExtractor.Epsilon

    private readonly int _featureSize;
    private long _count;
    private readonly double[] _sum;
    private readonly double[] _sumSq;

    public RunningMelNormalizer(int featureSize)
    {
        _featureSize = featureSize;
        _sum = new double[featureSize];
        _sumSq = new double[featureSize];
    }

    public void Add(IReadOnlyList<double[]> frames)
    {
        foreach (var f in frames)
        {
            for (var m = 0; m < _featureSize; m++)
            {
                _sum[m] += f[m];
                _sumSq[m] += f[m] * f[m];
            }
            _count++;
        }
    }

    /// <summary>Normalize <paramref name="frames"/> with the CURRENT running stats → [T, featureSize].</summary>
    public float[,] Normalize(IReadOnlyList<double[]> frames)
    {
        var output = new float[frames.Count, _featureSize];
        var divisor = _count > 1 ? _count - 1 : 1;
        for (var m = 0; m < _featureSize; m++)
        {
            var mean = _count > 0 ? _sum[m] / _count : 0.0;
            var variance = Math.Max((_sumSq[m] - _count * mean * mean) / divisor, 0.0);
            var invStd = 1.0 / (Math.Sqrt(variance) + Epsilon);
            for (var t = 0; t < frames.Count; t++)
                output[t, m] = (float)((frames[t][m] - mean) * invStd);
        }
        return output;
    }
}
