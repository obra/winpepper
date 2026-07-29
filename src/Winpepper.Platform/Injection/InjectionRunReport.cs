namespace Winpepper.Platform.Injection;

/// <summary>
/// Detailed outcome of one guarded injection run, for the per-dictation
/// timing summary. PacingWaitMs is the NOMINAL total of the inter-chunk
/// pause periods requested (sum of PeriodMsForChunk over invoked pauses);
/// the DeadlinePacer nets out send time at run time, and wall time is
/// measured by the caller's stopwatch. ChunksSent &lt; ChunksTotal on an
/// Interrupted/SendFailed run. ChunksTotal is 0 when the run never
/// reached chunking (NoForeground / BlockedElevated / mouse-held park).
/// </summary>
public readonly record struct InjectionRunReport(
    InjectionRunOutcome Outcome,
    int ChunksTotal,
    int ChunksSent,
    int PacingWaitMs);
