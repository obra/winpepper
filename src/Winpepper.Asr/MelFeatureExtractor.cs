namespace Winpepper.Asr;

/// <summary>
/// Mel feature extractor for Parakeet TDT v3.
/// Produces a [T, n_mels] float32 matrix in row-major order.
///
/// Plan 1 uses a hand-rolled O(n^2) rFFT — fine at n_fft=512.
/// Plan 2 will swap in NWaves' RealFft once its output layout is verified to match.
/// </summary>
public sealed class MelFeatureExtractor
{
    private readonly PreprocessorConfig _config;
    private readonly double[] _window;
    private readonly double[][] _melFilters;

    private const double MelOffset = 1.0 / (1 << 24); // 2^-24
    private const double Epsilon = 1e-5;
    private const double MelMin = 1e-30;

    public MelFeatureExtractor(PreprocessorConfig config)
    {
        _config = config;
        _window = BuildHannWindow(config.NFft, config.WinLength);
        _melFilters = BuildSlaneyMelFilters(config.NFft, config.FeatureSize, config.SamplingRate);
    }

    public float[,] Extract(ReadOnlySpan<float> samplesF32)
    {
        // 1) Preemphasis.
        var x = new double[samplesF32.Length];
        for (var i = 0; i < x.Length; i++) x[i] = samplesF32[i];
        for (var j = x.Length - 1; j >= 1; j--) x[j] -= _config.Preemphasis * x[j - 1];

        // 2) Centered framing with zero padding (matches the Python reference's mode='constant').
        var pad = _config.NFft / 2;
        var padded = new double[x.Length + 2 * pad];
        Array.Copy(x, 0, padded, pad, x.Length);

        var nFrames = (padded.Length - _config.NFft) / _config.HopLength + 1;
        var nBins = _config.NFft / 2 + 1;

        var logMel = new double[nFrames, _config.FeatureSize];
        var frame = new double[_config.NFft];
        var power = new double[nBins];

        for (var t = 0; t < nFrames; t++)
        {
            for (var k = 0; k < _config.NFft; k++)
                frame[k] = padded[t * _config.HopLength + k] * _window[k];

            HandRolledRfftPower(frame, _config.NFft, power);

            for (var m = 0; m < _config.FeatureSize; m++)
            {
                double acc = 0.0;
                var filter = _melFilters[m];
                for (var k = 0; k < nBins; k++) acc += power[k] * filter[k];
                logMel[t, m] = Math.Log(Math.Max(acc + MelOffset, MelMin));
            }
        }

        // 3) Per-utterance normalization, ddof=1 (numpy default ddof=1 when nFrames > 1).
        // Two-pass mean/variance for numerical stability — matches numpy's behavior.
        var mean = new double[_config.FeatureSize];
        for (var t = 0; t < nFrames; t++)
            for (var m = 0; m < _config.FeatureSize; m++)
                mean[m] += logMel[t, m];
        for (var m = 0; m < _config.FeatureSize; m++) mean[m] /= nFrames;

        var sumSqDev = new double[_config.FeatureSize];
        for (var t = 0; t < nFrames; t++)
            for (var m = 0; m < _config.FeatureSize; m++)
            {
                var d = logMel[t, m] - mean[m];
                sumSqDev[m] += d * d;
            }
        var divisor = nFrames > 1 ? nFrames - 1 : 1;

        var output = new float[nFrames, _config.FeatureSize];
        for (var m = 0; m < _config.FeatureSize; m++)
        {
            var variance = sumSqDev[m] / divisor;
            var std = Math.Sqrt(Math.Max(variance, 0)) + Epsilon;
            var invStd = 1.0 / std;
            for (var t = 0; t < nFrames; t++)
                output[t, m] = (float)((logMel[t, m] - mean[m]) * invStd);
        }
        return output;
    }

    private static void HandRolledRfftPower(ReadOnlySpan<double> frame, int n, double[] power)
    {
        // O(n^2) DFT — fine for n=512 (called once per frame, hundreds of frames per second).
        // Optimize in Plan 2 if hot.
        for (var k = 0; k <= n / 2; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                re += frame[t] * Math.Cos(angle);
                im += frame[t] * Math.Sin(angle);
            }
            power[k] = re * re + im * im;
        }
    }

    private static double[] BuildHannWindow(int nFft, int winLength)
    {
        var w = new double[nFft];
        var offset = (nFft - winLength) / 2;
        for (var i = 0; i < winLength; i++)
            w[offset + i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (winLength - 1));
        return w;
    }

    private static double[][] BuildSlaneyMelFilters(int nFft, int nMels, int sr)
    {
        static double HzToMel(double f) =>
            f < 1000.0
                ? f * 3.0 / 200.0
                : 15.0 + Math.Log(f / 1000.0) / (Math.Log(6.4) / 27.0);
        static double MelToHz(double m) =>
            m < 15.0
                ? m * 200.0 / 3.0
                : 1000.0 * Math.Exp((m - 15.0) * (Math.Log(6.4) / 27.0));

        var melMin = HzToMel(0);
        var melMax = HzToMel(sr / 2.0);
        var nBins = nFft / 2 + 1;

        var melPoints = new double[nMels + 2];
        var hzPoints = new double[nMels + 2];
        for (var i = 0; i < nMels + 2; i++)
        {
            melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);
            hzPoints[i] = MelToHz(melPoints[i]);
        }
        var bins = new double[nMels + 2];
        for (var i = 0; i < nMels + 2; i++) bins[i] = hzPoints[i] * nFft / sr;

        var filters = new double[nMels][];
        for (var m = 0; m < nMels; m++)
        {
            filters[m] = new double[nBins];
            double left = bins[m], center = bins[m + 1], right = bins[m + 2];
            for (var k = 0; k < nBins; k++)
            {
                if (k < left || k > right) continue;
                filters[m][k] = k <= center
                    ? (k - left) / (center - left + 1e-12)
                    : (right - k) / (right - center + 1e-12);
            }
            var enorm = 2.0 / (hzPoints[m + 2] - hzPoints[m]);
            for (var k = 0; k < nBins; k++) filters[m][k] *= enorm;
        }
        return filters;
    }
}
