namespace Winpepper.Asr.Transcription;

/// <summary>Per-dictation aggregate of the synchronous native streaming calls
/// (stream begin / feed / finalize). Complements the >=3 s TimedNativeCall
/// warnings: below that threshold calls are individually silent by design
/// (log-volume discipline), so the aggregate is what distinguishes "one
/// 2.9 s call" from "many 250 ms calls" after the fact.
/// <paramref name="Over250StartTicks"/>: absolute Environment.TickCount64 at the
/// START of each native call that took >= 250 ms, first
/// <see cref="Over250ListCap"/> only (bounded memory); <paramref name="Over250Overflow"/>
/// counts the rest. Consumers convert to recording-start offsets
/// (DictationTimingSummary.StampOver250).</summary>
/// <param name="Count">Total native calls this session.</param>
/// <param name="TotalMs">Sum of all native call durations.</param>
/// <param name="MaxMs">Slowest single call.</param>
/// <param name="CountOver250Ms">Calls taking >= 250 ms — already ~1.5x
/// real time for a 160 ms feed chunk, i.e. pathological yet silent today.</param>
public sealed record NativeCallStats(
    int Count,
    int TotalMs,
    int MaxMs,
    int CountOver250Ms,
    IReadOnlyList<long>? Over250StartTicks = null,
    int Over250Overflow = 0)
{
    public const int Over250ListCap = 16;
}

/// <summary>Optional side-channel on a streaming session that aggregates
/// native-call timings. Probed with <c>as</c> by StreamingDictationSession at
/// finish; sessions with no native calls simply don't implement it.</summary>
public interface INativeCallStatsSource
{
    /// <summary>Thread-safe snapshot of the aggregates so far.</summary>
    NativeCallStats NativeCallStats { get; }
}
