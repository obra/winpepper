namespace AsrLatencyBench;

/// <summary>
/// Bench argument validation. BCL-only so the same file compiles into
/// Winpepper.Asr.Tests (same pattern as EvalResults.cs).
/// </summary>
public static class BenchArgs
{
    /// <summary>Returns an error message when the --repeats value is invalid, null when it is usable.</summary>
    public static string? ValidateRepeats(int repeats)
        => repeats >= 1 ? null : $"--repeats must be >= 1 (got {repeats})";

    public static string? ValidateMaxClips(int maxClips)
        => maxClips >= 0 ? null : $"--max-clips must be >= 0 (0 = all clips), got {maxClips}";

    public static string? ValidateTimeBudgetMinutes(double minutes)
        => minutes >= 0 ? null : $"--time-budget-minutes must be >= 0 (0 = no budget), got {minutes}";

    public static string? ValidatePasses(int minPasses, int maxPasses)
    {
        if (minPasses < 1) return $"--min-passes must be >= 1, got {minPasses}";
        if (maxPasses < 0) return $"--max-passes must be >= 0 (0 = unlimited), got {maxPasses}";
        if (maxPasses != 0 && maxPasses < minPasses)
            return $"--max-passes ({maxPasses}) must be 0 or >= --min-passes ({minPasses})";
        return null;
    }

    public static string? ValidateStopCondition(int maxPasses, double timeBudgetMinutes)
        => maxPasses != 0 || timeBudgetMinutes != 0
            ? null
            : "--max-passes 0 (unlimited) with --time-budget-minutes 0 (no budget) leaves no stop condition; set at least one bound";

    /// <summary>Effective pass controls after applying the --repeats rule. When
    /// <see cref="Error"/> is non-null the other fields are the untouched inputs and
    /// the run must be rejected. <see cref="LegacyMode"/> is true only for the
    /// pre-convergence semantics (announce it loudly in the run header).</summary>
    public sealed record RepeatsResolution(
        double TimeBudgetMinutes, int MinPasses, int MaxPasses, bool LegacyMode, string? Error);

    /// <summary>
    /// Resolves --repeats against the convergence flags. The rule (also documented in
    /// the Program.cs usage header):
    /// <list type="bullet">
    /// <item>--repeats N with NO convergence flag (--time-budget-minutes / --min-passes /
    /// --max-passes): legacy pre-convergence semantics -- each clip runs exactly N times
    /// in a bounded run (min-passes = max-passes = N, time budget disabled, so
    /// convergence can neither stop the run early nor extend it).</item>
    /// <item>--repeats N with --time-budget-minutes and/or --min-passes: N keeps its new
    /// meaning, the pass cap (max-passes = N); convergence semantics apply.</item>
    /// <item>--repeats with an explicit --max-passes: rejected as ambiguous -- both flags
    /// set the pass cap.</item>
    /// </list>
    /// </summary>
    public static RepeatsResolution ResolveRepeats(
        bool repeatsSet, int repeats,
        bool timeBudgetSet, double timeBudgetMinutes,
        bool minPassesSet, int minPasses,
        bool maxPassesSet, int maxPasses)
    {
        if (!repeatsSet)
            return new(timeBudgetMinutes, minPasses, maxPasses, LegacyMode: false, Error: null);
        if (maxPassesSet)
            return new(timeBudgetMinutes, minPasses, maxPasses, LegacyMode: false,
                Error: "--repeats and --max-passes both set the pass cap; pass exactly one of them");
        if (timeBudgetSet || minPassesSet)
            return new(timeBudgetMinutes, minPasses, repeats, LegacyMode: false, Error: null);
        return new(TimeBudgetMinutes: 0, MinPasses: repeats, MaxPasses: repeats,
            LegacyMode: true, Error: null);
    }
}
