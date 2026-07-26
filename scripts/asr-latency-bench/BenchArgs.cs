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
}
