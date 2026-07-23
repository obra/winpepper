namespace Winpepper.Core.Audio;

/// <summary>
/// Pure-managed voice-level meter: converts raw mono float frames into a
/// smoothed 0..1 loudness value suitable for driving the status pill's voice
/// meter. Fast attack (jumps up quickly on speech) and slower decay (falls
/// gently so the meter pulses naturally). No timers, no UI — fully testable.
/// </summary>
public sealed class LevelMeterModel
{
    private readonly double _attack;
    private readonly double _decay;
    private double _level;

    public LevelMeterModel(double attack = 0.5, double decay = 0.15)
    {
        _attack = Clamp01(attack);
        _decay = Clamp01(decay);
    }

    /// <summary>Current smoothed level, 0..1.</summary>
    public double Level => _level;

    /// <summary>Absolute-peak of a frame, clamped to 0..1.</summary>
    public static double Peak(ReadOnlySpan<float> frame)
    {
        double peak = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            var v = Math.Abs((double)frame[i]);
            if (v > peak) peak = v;
        }
        return peak > 1.0 ? 1.0 : peak;
    }

    /// <summary>
    /// Push one frame; returns the new smoothed level (0..1). Rising peaks use
    /// the attack coefficient, falling peaks use the (slower) decay coefficient.
    /// </summary>
    public double Push(ReadOnlySpan<float> frame)
    {
        var target = Peak(frame);
        var coeff = target > _level ? _attack : _decay;
        _level += (target - _level) * coeff;
        if (_level < 0) _level = 0;
        if (_level > 1) _level = 1;
        return _level;
    }

    /// <summary>Snap the level back to zero (e.g. when recording stops).</summary>
    public void Reset() => _level = 0;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
