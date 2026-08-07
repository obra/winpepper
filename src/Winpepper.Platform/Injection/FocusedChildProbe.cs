namespace Winpepper.Platform.Injection;

/// <summary>
/// Pure double-sample logic for the focused-child capture: sample the
/// target GUI thread's focus window twice, >= 30 ms apart; stable = both
/// samples equal and nonzero (design doc §2.2). Environment access is
/// injected so every path is unit-testable on Linux; the production
/// sampler is MessageDelivery.SampleFocusedChild.
/// </summary>
internal static class FocusedChildProbe
{
    internal const int SampleGapMs = 30;

    public static FocusedChildCapture Capture(
        long foregroundHwnd,
        Func<long, long> sampleFocusedChild,
        Action<int> sleep)
    {
        var first = sampleFocusedChild(foregroundHwnd);
        // A zero first sample already determines the verdict (unstable):
        // skip the gap and the second sample.
        if (first == 0) return new FocusedChildCapture(0, false);
        sleep(SampleGapMs);
        var second = sampleFocusedChild(foregroundHwnd);
        var stable = first == second;
        return new FocusedChildCapture(stable ? first : 0, stable);
    }
}
