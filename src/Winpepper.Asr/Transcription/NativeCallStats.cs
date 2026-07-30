namespace Winpepper.Asr.Transcription;

/// <summary>Per-dictation aggregate of the synchronous native streaming calls
/// (stream begin / feed / finalize). Complements the >=3 s TimedNativeCall
/// warnings: below that threshold calls are individually silent by design
/// (log-volume discipline), so the aggregate is what distinguishes "one
/// 2.9 s call" from "many 250 ms calls" after the fact.</summary>
/// <param name="Count">Total native calls this session.</param>
/// <param name="TotalMs">Sum of all native call durations.</param>
/// <param name="MaxMs">Slowest single call.</param>
/// <param name="CountOver250Ms">Calls taking >= 250 ms — already ~1.5x
/// real time for a 160 ms feed chunk, i.e. pathological yet silent today.</param>
public sealed record NativeCallStats(int Count, int TotalMs, int MaxMs, int CountOver250Ms);

/// <summary>Optional side-channel on a streaming session that aggregates
/// native-call timings. Probed with <c>as</c> by StreamingDictationSession at
/// finish; sessions with no native calls simply don't implement it.</summary>
public interface INativeCallStatsSource
{
    /// <summary>Thread-safe snapshot of the aggregates so far.</summary>
    NativeCallStats NativeCallStats { get; }
}
