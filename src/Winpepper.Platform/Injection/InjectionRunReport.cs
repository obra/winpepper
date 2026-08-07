namespace Winpepper.Platform.Injection;

/// <summary>
/// Detailed outcome of one guarded injection run, for the per-dictation
/// timing summary. PacingWaitMs is the NOMINAL total of the inter-chunk
/// pause periods requested (sum of PeriodMsForChunk over invoked pauses);
/// the DeadlinePacer nets out send time at run time, and wall time is
/// measured by the caller's stopwatch. ChunksSent &lt; ChunksTotal on an
/// Interrupted/SendFailed run. ChunksTotal is 0 when the run never
/// reached chunking (NoForeground / BlockedElevated / mouse-held park).
/// Via is the delivery channel that carried (or would have carried) the
/// run; it defaults to VkPacket -- including for default(InjectionRunReport),
/// which is why DeliveryChannel.VkPacket is 0. GatesSummary is the
/// "&lt;rung&gt;:&lt;reason&gt;" comma-list of rungs that gated out
/// (design doc 2026-08-06 §2.4); null/empty when the first rung delivered
/// or the run parked before routing.
/// </summary>
public readonly record struct InjectionRunReport(
    InjectionRunOutcome Outcome,
    int ChunksTotal,
    int ChunksSent,
    int PacingWaitMs,
    DeliveryChannel Via = DeliveryChannel.VkPacket,
    string? GatesSummary = null);
