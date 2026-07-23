using System;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure mapping from a smoothed input level (0..1) to how many discrete meter
/// bars should light in the status pill's voice meter. Silence lights zero
/// bars; any audible level lights at least one; full scale lights them all.
/// No UI, no timers — fully testable.
/// </summary>
public static class VoiceMeter
{
    /// <summary>
    /// Number of lit bars for <paramref name="level"/> over
    /// <paramref name="barCount"/> total bars. <paramref name="level"/> is
    /// clamped to 0..1. Returns 0 at (or below) silence, otherwise
    /// ceil(level * barCount) clamped to [1, barCount].
    /// </summary>
    /// <summary>
    /// Perceptual (dB) mapping of a linear 0..1 amplitude to a 0..1 display
    /// value. Speech peaks at normal mic gain live around 0.05..0.3 linear, so
    /// a linear meter sits stuck on its first bar. Mapping the dB range
    /// -50 dBFS (floor, effectively silence) .. -10 dBFS (loud speech) to 0..1
    /// spreads normal speech across the full meter.
    /// </summary>
    public static double Perceptual(double linear)
    {
        const double FloorDb = -50.0;
        const double CeilingDb = -10.0;

        if (linear <= 0 || double.IsNaN(linear)) return 0;
        if (linear > 1) linear = 1;

        var db = 20.0 * Math.Log10(linear);
        var norm = (db - FloorDb) / (CeilingDb - FloorDb);
        return norm < 0 ? 0 : norm > 1 ? 1 : norm;
    }

    public static int BarsLit(double level, int barCount)
    {
        if (barCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(barCount));

        if (level <= 0 || double.IsNaN(level))
            return 0;
        if (level > 1)
            level = 1;

        var lit = (int)Math.Ceiling(level * barCount);
        if (lit < 1) lit = 1;
        if (lit > barCount) lit = barCount;
        return lit;
    }
}
