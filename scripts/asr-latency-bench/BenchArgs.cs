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
}
